using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Navegador;

/// <summary>
/// El tirador en forma de gota del explorador de archivos.
/// En reposo es una tira finita aplastada contra el borde y bastante transparente. Al acercar el mouse se despierta:
/// la gota se estira hacia el cursor y su punta lo sigue de arriba a abajo, mientras la base queda pegada al borde.
/// </summary>
public sealed partial class MainWindow
{
    private const double HandleWidth = 16;   // debe coincidir con el ancho de la franja en el XAML
    private const double IdleBulge = 3;      // grosor de la tira en reposo
    private const double IdleHalfHeight = 20;
    private const double HoverHalfHeight = 34;

    // valores actuales (se animan) y valores a los que van
    private double _dropApexY, _dropBaseY, _dropBulge = IdleBulge, _dropHalf = IdleHalfHeight, _dropOpacity = 0.9;
    private double _tApexY, _tBaseY, _tBulge = IdleBulge, _tHalf = IdleHalfHeight, _tOpacity = 0.9;
    private double _dropIdleOpacity = 0.9;   // al abrir la app se ve bien para que se note; después se esconde
    private bool _dropHover, _dropPressed, _dropAnimating, _dropReady;
    private Windows.Foundation.Point _dropPointer;
    private DateTime _dropLast;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dropFadeTimer;

    // ───────────── Eventos ─────────────

    private void DropHandle_Tapped(object sender, TappedRoutedEventArgs e) => SetDrawer(!_drawerOpen);

    private void DropHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _dropHover = true;
        _dropPointer = e.GetCurrentPoint(DropHandle).Position;
        SetDropTargets();
    }

    private void DropHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _dropPointer = e.GetCurrentPoint(DropHandle).Position;
        SetDropTargets();
    }

    private void DropHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _dropHover = false;
        _dropPressed = false;
        SetDropTargets();
    }

    private void DropHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dropPressed = true;
        SetDropTargets();
    }

    private void DropHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dropPressed = false;
        SetDropTargets();
    }

    private void DropHandle_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var h = DropHandle.ActualHeight;
        if (h <= 0) return;

        if (!_dropReady)
        {
            _dropReady = true;
            _dropApexY = _dropBaseY = h / 2;
            DrawDrop();

            // Después de unos segundos sin usarla, la gota se desvanece casi del todo
            _dropFadeTimer = DispatcherQueue.CreateTimer();
            _dropFadeTimer.Interval = TimeSpan.FromSeconds(3);
            _dropFadeTimer.IsRepeating = false;
            _dropFadeTimer.Tick += (_, _) =>
            {
                _dropIdleOpacity = 0.35;
                SetDropTargets();
            };
            _dropFadeTimer.Start();
        }
        SetDropTargets();
    }

    // ───────────── Hacia dónde va la gota ─────────────

    private void SetDropTargets()
    {
        var h = DropHandle.ActualHeight;
        if (h <= 0) return;

        if (_dropHover)
        {
            // La base se queda cerca del cursor y la punta lo persigue; cuanto más lejos del borde está el mouse,
            // más se estira la gota hacia él.
            var limit = Math.Max(HoverHalfHeight, h - HoverHalfHeight);
            _tApexY = Math.Clamp(_dropPointer.Y, 8, Math.Max(8, h - 8));
            _tBaseY = Math.Clamp(_dropPointer.Y, HoverHalfHeight, limit);
            _tBulge = _dropPressed ? 6 : Math.Clamp(_dropPointer.X + 5, 9, HandleWidth - 1);
            _tHalf = HoverHalfHeight;
            _tOpacity = 1.0;
        }
        else
        {
            _tApexY = _tBaseY = h / 2;
            _tBulge = IdleBulge;
            _tHalf = IdleHalfHeight;
            _tOpacity = _dropIdleOpacity;
        }
        StartDropAnimation();
    }

    // ───────────── Animación (resorte suave, solo mientras algo se mueve) ─────────────

    private void StartDropAnimation()
    {
        if (_dropAnimating) return;
        _dropAnimating = true;
        _dropLast = DateTime.UtcNow;
        CompositionTarget.Rendering += DropFrame;
    }

    private void DropFrame(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.05, (now - _dropLast).TotalSeconds);
        _dropLast = now;

        double Step(double current, double target, double rate) =>
            current + (target - current) * (1 - Math.Exp(-rate * dt));

        // la punta reacciona rápido y la base más lento: eso da el efecto de gota que se estira
        _dropApexY = Step(_dropApexY, _tApexY, 22);
        _dropBaseY = Step(_dropBaseY, _tBaseY, 9);
        _dropBulge = Step(_dropBulge, _tBulge, 16);
        _dropHalf = Step(_dropHalf, _tHalf, 10);
        _dropOpacity = Step(_dropOpacity, _tOpacity, 6);

        DrawDrop();

        var settled =
            Math.Abs(_dropApexY - _tApexY) < 0.2 &&
            Math.Abs(_dropBaseY - _tBaseY) < 0.2 &&
            Math.Abs(_dropBulge - _tBulge) < 0.05 &&
            Math.Abs(_dropHalf - _tHalf) < 0.2 &&
            Math.Abs(_dropOpacity - _tOpacity) < 0.01;
        if (settled)
        {
            _dropAnimating = false;
            CompositionTarget.Rendering -= DropFrame;
        }
    }

    // ───────────── Dibujo ─────────────

    private void DrawDrop()
    {
        var h = DropHandle.ActualHeight;
        if (h <= 0) return;

        var half = Math.Max(4, _dropHalf);
        var baseY = Math.Clamp(_dropBaseY, half, Math.Max(half, h - half));
        var top = baseY - half;
        var bottom = baseY + half;
        var apex = Math.Clamp(_dropApexY, top + 4, bottom - 4);
        var w = Math.Max(1.5, _dropBulge);

        // Pegada al borde izquierdo (x = 0) y abultada hacia la derecha; la punta (apex) puede quedar corrida
        // hacia arriba o abajo de la base, y así la gota se ve "tirada" hacia el mouse.
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(0, top), IsClosed = true };
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Windows.Foundation.Point(w * 0.15, top),
            Point2 = new Windows.Foundation.Point(w, apex - (apex - top) * 0.55),
            Point3 = new Windows.Foundation.Point(w, apex)
        });
        figure.Segments.Add(new BezierSegment
        {
            Point1 = new Windows.Foundation.Point(w, apex + (bottom - apex) * 0.55),
            Point2 = new Windows.Foundation.Point(w * 0.15, bottom),
            Point3 = new Windows.Foundation.Point(0, bottom)
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        DropPath.Data = geometry;
        DropPath.Opacity = _dropOpacity;

        // La flecha aparece solo cuando la gota está bien estirada
        DropHandleIcon.Opacity = Math.Clamp((w - 8) / 5, 0, 1) * _dropOpacity;
        Canvas.SetLeft(DropHandleIcon, 1.5);
        Canvas.SetTop(DropHandleIcon, apex - 7);
    }
}
