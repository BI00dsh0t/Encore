using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

public sealed unsafe partial class FrameScaler
{
    /// <summary>A fixed set of views keyed by texture and slice, each holding its texture alive; when full, the oldest goes.</summary>
    private sealed class ViewCache(int capacity)
    {
        private readonly nint[] _textures = new nint[capacity];
        private readonly uint[] _slices = new uint[capacity];
        private readonly nint[] _views = new nint[capacity];
        private int _nextEviction;

        public nint Find(nint texture, uint slice)
        {
            for (var i = 0; i < _views.Length; i++)
            {
                if (_views[i] != 0 && _textures[i] == texture && _slices[i] == slice)
                {
                    return _views[i];
                }
            }

            return 0;
        }

        public void Add(nint texture, uint slice, nint view)
        {
            var free = Array.IndexOf(_views, (nint)0);
            if (free < 0)
            {
                free = _nextEviction;
                _nextEviction = (_nextEviction + 1) % _views.Length;
                ReleaseAt(free);
            }

            ((IUnknown*)texture)->AddRef();
            _textures[free] = texture;
            _slices[free] = slice;
            _views[free] = view;
        }

        public void Remove(nint texture)
        {
            for (var i = 0; i < _views.Length; i++)
            {
                if (_textures[i] == texture)
                {
                    ReleaseAt(i);
                }
            }
        }

        public void Clear()
        {
            for (var i = 0; i < _views.Length; i++)
            {
                ReleaseAt(i);
            }
        }

        private void ReleaseAt(int index)
        {
            if (_views[index] != 0)
            {
                ((IUnknown*)_views[index])->Release();
                ((IUnknown*)_textures[index])->Release();
                _views[index] = 0;
                _textures[index] = 0;
            }
        }
    }
}
