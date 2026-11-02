namespace VideoEditor.Core.Ffmpeg;

/// <summary>固定容量的快取，滿了就丟掉最久沒用的項目。用於拖曳預覽時重用已解出的影格。</summary>
public sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = [];

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        if (_map.Remove(key, out var existing)) _order.Remove(existing);
        _map[key] = _order.AddFirst((key, value));
        if (_map.Count <= capacity) return;
        _map.Remove(_order.Last!.Value.Key);
        _order.RemoveLast();
    }
}
