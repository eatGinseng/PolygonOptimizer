using CommunityToolkit.Mvvm.ComponentModel;
using HelixToolkit.SharpDX.Model.Scene;
using System;
using System.Collections.Generic;

namespace FileLoadDemo;

public partial class MaterialItem : ObservableObject
{
    public string Name { get; }
    public List<MeshNode> MeshNodes { get; } = new();
    public Action? VisibilityChanged { get; set; }

    [ObservableProperty]
    private bool isVisible = true;

    partial void OnIsVisibleChanged(bool value)
    {
        foreach (var node in MeshNodes)
        {
            node.Visible = value;
        }
        VisibilityChanged?.Invoke();
    }

    public MaterialItem(string name)
    {
        Name = name;
    }
}
