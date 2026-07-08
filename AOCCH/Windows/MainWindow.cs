using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AOCCH.Windows;

public class MainWindow : Window, IDisposable
{
    // We give this window a hidden ID using ##.
    // The user will see "Another Occult Crescent Helper" as window title,
    // but for ImGui the ID is "Another Occult Crescent Helper##Main".
    public MainWindow()
        : base("Another Occult Crescent Helper##Main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void Dispose() { }

    public override void Draw() { }
}
