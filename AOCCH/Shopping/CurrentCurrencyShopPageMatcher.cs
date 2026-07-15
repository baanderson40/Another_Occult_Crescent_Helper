using System.Collections.Generic;
using System.Linq;

namespace AOCCH.Shopping;

public sealed class CurrentCurrencyShopMatch
{
    public required ShopCurrencyPageDefinition Page { get; init; }
    public required ShopCurrencyTabDefinition Tab { get; init; }
    public required int ReportedTabId { get; init; }
    public required string Reason { get; init; }
}

public sealed class CurrentCurrencyShopPageMatcher
{
    public bool TryMatch(LiveShopSnapshot snapshot, out CurrentCurrencyShopMatch? match, out string reason)
    {
        match = null;
        reason = string.Empty;

        if (!snapshot.IsShopExchangeCurrencyOpen || snapshot.CurrencyItemId == 0 || snapshot.ShopEntries.Count == 0)
        {
            reason = "No supported currency shop is open.";
            return false;
        }

        var liveEntriesById = snapshot.ShopEntries.ToDictionary(entry => entry.ItemId, entry => entry.RowIndex);
        var candidates = new List<(ShopCurrencyPageDefinition Page, ShopCurrencyTabDefinition Tab, int OverlapCount, int RowMatchCount, int ReportedTabBonus)>();

        foreach (var candidatePage in ShopCurrencyCatalog.Pages)
        {
            if (candidatePage.CurrencyItemId != snapshot.CurrencyItemId)
            {
                continue;
            }

            foreach (var candidateTab in candidatePage.Tabs)
            {
                var overlapCount = 0;
                var rowMatchCount = 0;
                foreach (var item in candidateTab.Items)
                {
                    if (!liveEntriesById.TryGetValue(item.ItemId, out var liveRowIndex))
                    {
                        continue;
                    }

                    overlapCount++;
                    if (liveRowIndex == item.RowIndex)
                    {
                        rowMatchCount++;
                    }
                }

                if (overlapCount > 0)
                {
                    var reportedTabBonus = candidateTab.TabId == snapshot.SelectedTabId ? 1 : 0;
                    candidates.Add((candidatePage, candidateTab, overlapCount, rowMatchCount, reportedTabBonus));
                }
            }
        }

        if (candidates.Count == 0)
        {
            reason = $"No supported page/tab definition matched currency item {snapshot.CurrencyItemId} for the currently visible entries.";
            return false;
        }

        var orderedCandidates = candidates
            .OrderByDescending(candidate => candidate.OverlapCount)
            .ThenByDescending(candidate => candidate.RowMatchCount)
            .ThenByDescending(candidate => candidate.ReportedTabBonus)
            .ToList();

        var bestCandidate = orderedCandidates[0];
        if (bestCandidate.OverlapCount < 2 && bestCandidate.RowMatchCount < 1)
        {
            reason = "The current page match confidence is too low.";
            return false;
        }

        if (orderedCandidates.Count > 1)
        {
            var secondCandidate = orderedCandidates[1];
            if (secondCandidate.OverlapCount == bestCandidate.OverlapCount
                && secondCandidate.RowMatchCount == bestCandidate.RowMatchCount
                && secondCandidate.ReportedTabBonus == bestCandidate.ReportedTabBonus)
            {
                reason = $"The current shop tab is ambiguous between {bestCandidate.Page.MenuLabel}/{bestCandidate.Tab.TabLabel} and {secondCandidate.Page.MenuLabel}/{secondCandidate.Tab.TabLabel}.";
                return false;
            }
        }

        match = new CurrentCurrencyShopMatch
        {
            Page = bestCandidate.Page,
            Tab = bestCandidate.Tab,
            ReportedTabId = snapshot.SelectedTabId,
            Reason = $"Matched {bestCandidate.Page.MenuLabel}/{bestCandidate.Tab.TabLabel} with overlap={bestCandidate.OverlapCount} rowMatches={bestCandidate.RowMatchCount} reportedTab={snapshot.SelectedTabId}.",
        };
        reason = match.Reason;
        return true;
    }
}
