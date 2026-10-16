using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SRdeckPlugin.Meshtastic.ViewModels;

/// <summary>
/// Owns the UI-thread node and map-point indexes together with their displayed collections.
/// Selection, filters and dispatcher scheduling remain responsibilities of the presentation.
/// </summary>
internal sealed class MeshtasticNodeStore
{
    private readonly Dictionary<uint, MeshtasticNodeDisplayItem> _nodes = new();
    private readonly Dictionary<uint, MeshtasticMapPoint> _mapPoints = new();

    public ObservableCollection<MeshtasticNodeDisplayItem> Nodes { get; } = new();
    public ObservableCollection<MeshtasticMapPoint> MapPoints { get; } = new();

    public bool TryGetNode(uint id, [NotNullWhen(true)] out MeshtasticNodeDisplayItem? node) =>
        _nodes.TryGetValue(id, out node);

    public bool TryGetMapPoint(uint id, [NotNullWhen(true)] out MeshtasticMapPoint? point) =>
        _mapPoints.TryGetValue(id, out point);

    public MeshtasticNodeDisplayItem GetOrAddNode(uint id, out bool added)
    {
        added = !_nodes.TryGetValue(id, out MeshtasticNodeDisplayItem? node);
        if (added)
        {
            node = new MeshtasticNodeDisplayItem(id);
            _nodes.Add(id, node);
            Nodes.Insert(0, node);
        }
        return node!;
    }

    public void AddRestoredNode(MeshtasticNodeDisplayItem node)
    {
        _nodes[node.NodeNumber] = node;
        Nodes.Add(node);
    }

    public MeshtasticMapPoint GetOrAddMapPoint(uint id)
    {
        if (!_mapPoints.TryGetValue(id, out MeshtasticMapPoint? point))
        {
            point = new MeshtasticMapPoint(id);
            _mapPoints[id] = point;
            MapPoints.Add(point);
        }
        return point;
    }

    public void RemoveNode(MeshtasticNodeDisplayItem node)
    {
        Nodes.Remove(node);
        if (_nodes.TryGetValue(node.NodeNumber, out MeshtasticNodeDisplayItem? cached) && ReferenceEquals(cached, node))
            _nodes.Remove(node.NodeNumber);
        if (_mapPoints.Remove(node.NodeNumber, out MeshtasticMapPoint? point)) MapPoints.Remove(point);
    }

    public void Clear()
    {
        Nodes.Clear();
        _nodes.Clear();
        MapPoints.Clear();
        _mapPoints.Clear();
    }
}
