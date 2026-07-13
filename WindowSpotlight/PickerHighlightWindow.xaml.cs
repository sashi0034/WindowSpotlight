using System.Windows;
using System.Windows.Interop;

namespace WindowSpotlight;

public partial class PickerHighlightWindow : Window
{
    private nint _handle;

    public PickerHighlightWindow()
    {
        InitializeComponent();
    }

    internal void ShowAround(PixelRect bounds)
    {
        if (!IsVisible)
        {
            Show();
        }

        NativeMethods.SetWindowPos(
            _handle,
            NativeMethods.HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProcedure);
        var extendedStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            new nint(extendedStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow |
                     NativeMethods.WsExTransparent));
    }

    private static nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest)
        {
            handled = true;
            return NativeMethods.HtTransparent;
        }

        return 0;
    }
}
