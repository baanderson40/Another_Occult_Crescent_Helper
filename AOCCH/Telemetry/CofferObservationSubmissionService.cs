using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AOCCH.Logging;
using AOCCH.Scanning;

namespace AOCCH.Telemetry;

public sealed record CofferObservationRequest(
    uint TerritoryId,
    uint? DataId,
    uint? MapId,
    float WorldX,
    float WorldY,
    float WorldZ,
    string? CofferType,
    string InstallationHash,
    string PluginVersion,
    string? GameVersion,
    DateTimeOffset ObservedAtUtc);

public sealed class CofferObservationSubmissionService : IDisposable
{
    private const int MaximumPendingObservations = 500;
    private const int MaximumAttempts = 5;
    private const string SubmissionUrl = "https://aocch-coffer-api.baanderson40.workers.dev/api/v1/observations";
    private static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(60),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HttpClient httpClient;
    private readonly Configuration configuration;
    private readonly AocchLogger logger;
    private readonly string queuePath;
    private readonly object gate = new();
    private readonly List<PendingObservation> pending = [];
    private readonly SemaphoreSlim processGate = new(1, 1);
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly CancellationTokenSource disposeCancellation = new();
    private readonly string pluginVersion;
    private readonly string installationHash;
    private readonly Dictionary<ObservationKey, DateTimeOffset> recentlyEnqueued = [];
    private bool disposed;

    public CofferObservationSubmissionService(
        string configurationDirectory,
        Configuration configuration,
        AocchLogger logger,
        string pluginVersion)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.pluginVersion = pluginVersion;
        queuePath = Path.Combine(configurationDirectory, "coffer-observation-queue.json");
        installationHash = LoadOrCreateInstallationHash(configurationDirectory);
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        LoadQueue();
        if (configuration.EnableCofferObservationSubmission && IsValidSubmissionUri())
        {
            SignalProcessing();
        }
    }

    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }

    public void Enqueue(ScannerSnapshot snapshot, VisibleCoffer coffer, string source)
    {
        if (disposed || !configuration.EnableCofferObservationSubmission || !IsValidSubmissionUri())
        {
            return;
        }

        var cofferType = string.IsNullOrWhiteSpace(coffer.Name)
            ? null
            : coffer.Name.Trim() is { Length: > 64 } name
                ? name[..64]
                : coffer.Name.Trim();

        var observation = new CofferObservationRequest(
            snapshot.TerritoryTypeId,
            coffer.DataId,
            null,
            coffer.Position.X,
            coffer.Position.Y,
            coffer.Position.Z,
            cofferType,
            installationHash,
            pluginVersion,
            null,
            DateTimeOffset.UtcNow);

        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            RemoveExpiredLocked(now);
            foreach (var key in recentlyEnqueued
                         .Where(entry => now - entry.Value > TimeSpan.FromSeconds(30))
                         .Select(entry => entry.Key)
                         .ToList())
            {
                recentlyEnqueued.Remove(key);
            }
            var eventKey = new ObservationKey(snapshot.TerritoryTypeId, coffer.GameObjectId, coffer.Position);
            if (recentlyEnqueued.ContainsKey(eventKey))
            {
                logger.Debug($"[CofferObservation] op=deduplicated source={source} objectId={coffer.GameObjectId:X}");
                return;
            }

            recentlyEnqueued[eventKey] = now;
            while (pending.Count >= MaximumPendingObservations)
            {
                pending.RemoveAt(0);
            }

            pending.Add(new PendingObservation
            {
                Observation = observation,
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow,
            });
            PersistLocked();
        }

        logger.Info($"[CofferObservation] op=queued source={source} territoryId={snapshot.TerritoryTypeId} position=<{coffer.Position.X:0.0},{coffer.Position.Y:0.0},{coffer.Position.Z:0.0}> pending={PendingCount}");
        SignalProcessing();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        disposeCancellation.Cancel();
        SignalProcessing();
        httpClient.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        if (disposed || !await processGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            while (!disposed && !disposeCancellation.IsCancellationRequested)
            {
                PendingObservation? item;
                TimeSpan? wait;
                lock (gate)
                {
                    RemoveExpiredLocked(DateTimeOffset.UtcNow);
                    item = pending.OrderBy(entry => entry.NextAttemptAtUtc).FirstOrDefault();
                    wait = item == null
                        ? null
                        : item.NextAttemptAtUtc - DateTimeOffset.UtcNow;
                }

                if (item == null)
                {
                    await wakeSignal.WaitAsync(disposeCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                if (wait > TimeSpan.Zero)
                {
                    await wakeSignal.WaitAsync(wait.Value, disposeCancellation.Token).ConfigureAwait(false);
                    continue;
                }

                var succeeded = await TrySendAsync(item, disposeCancellation.Token).ConfigureAwait(false);
                lock (gate)
                {
                    if (succeeded)
                    {
                        pending.Remove(item);
                    }
                    else
                    {
                        item.Attempts++;
                        if (item.Attempts >= MaximumAttempts)
                        {
                            pending.Remove(item);
                            logger.Warning("[CofferObservation] op=discarded reason=max-attempts");
                        }
                        else
                        {
                            item.NextAttemptAtUtc = DateTimeOffset.UtcNow + RetryDelays[Math.Min(item.Attempts - 1, RetryDelays.Length - 1)];
                        }
                    }

                    PersistLocked();
                }
            }
        }
        catch (OperationCanceledException) when (disposeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            processGate.Release();
        }
    }

    private async Task<bool> TrySendAsync(PendingObservation item, CancellationToken cancellationToken)
    {
        if (!IsValidSubmissionUri())
        {
            return false;
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                SubmissionUrl,
                item.Observation,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                logger.Info($"[CofferObservation] op=submitted status={(int)response.StatusCode} duplicate={response.StatusCode == System.Net.HttpStatusCode.OK}");
                return true;
            }

            logger.Warning($"[CofferObservation] op=submit-failed status={(int)response.StatusCode} attempt={item.Attempts + 1}");
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warning($"[CofferObservation] op=submit-timeout attempt={item.Attempts + 1}");
            return false;
        }
        catch (HttpRequestException ex)
        {
            logger.Warning($"[CofferObservation] op=submit-request-failed attempt={item.Attempts + 1} error={ex.Message}");
            return false;
        }
        catch (ObjectDisposedException) when (disposed)
        {
            return false;
        }
    }

    private bool IsValidSubmissionUri()
        => Uri.TryCreate(SubmissionUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.AbsolutePath.EndsWith("/api/v1/observations", StringComparison.Ordinal);

    private void SignalProcessing()
    {
        try
        {
            wakeSignal.Release();
            _ = ProcessQueueAsync();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LoadQueue()
    {
        try
        {
            if (!File.Exists(queuePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<PendingObservation>>(File.ReadAllText(queuePath), SerializerOptions) ?? [];
            lock (gate)
            {
                pending.AddRange(loaded.TakeLast(MaximumPendingObservations));
                RemoveExpiredLocked(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"[CofferObservation] op=queue-load-failed error={ex.Message}");
        }
    }

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(queuePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = queuePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(pending, SerializerOptions));
            File.Move(temporaryPath, queuePath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.Warning($"[CofferObservation] op=queue-save-failed error={ex.Message}");
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
        => pending.RemoveAll(item => now - item.EnqueuedAtUtc > MaximumRetention);

    private static string LoadOrCreateInstallationHash(string configurationDirectory)
    {
        var identifierPath = Path.Combine(configurationDirectory, "coffer-installation-id.txt");
        string identifier;
        try
        {
            identifier = File.Exists(identifierPath) ? File.ReadAllText(identifierPath).Trim() : string.Empty;
            if (!Guid.TryParse(identifier, out _))
            {
                identifier = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(configurationDirectory);
                File.WriteAllText(identifierPath, identifier);
            }
        }
        catch
        {
            identifier = Guid.NewGuid().ToString("N");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier))).ToLowerInvariant();
    }

    private sealed class PendingObservation
    {
        public CofferObservationRequest Observation { get; set; } = new(
            0,
            null,
            null,
            0,
            0,
            0,
            null,
            string.Empty,
            string.Empty,
            null,
            DateTimeOffset.MinValue);

        public int Attempts { get; set; }
        public DateTimeOffset EnqueuedAtUtc { get; set; }
        public DateTimeOffset NextAttemptAtUtc { get; set; }
    }

    private readonly record struct ObservationKey(uint TerritoryId, ulong ObjectId, System.Numerics.Vector3 Position)
    {
        public bool Equals(ObservationKey other)
            => TerritoryId == other.TerritoryId
                && ObjectId == other.ObjectId
                && MathF.Abs(Position.X - other.Position.X) <= 0.1f
                && MathF.Abs(Position.Y - other.Position.Y) <= 0.1f
                && MathF.Abs(Position.Z - other.Position.Z) <= 0.1f;

        public override int GetHashCode()
            => HashCode.Combine(TerritoryId, ObjectId);
    }
}
