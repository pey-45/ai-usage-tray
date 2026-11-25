# Cambio: un solo widget en la barra de tareas en lugar de iconos de bandeja

Los iconos de bandeja miden 16×16 px y Windows los separa: no caben números grandes. Sustituye los seis
`NotifyIcon` por **una ventanita propia colocada encima de la barra de tareas**, justo a la izquierda del
área de notificación. Cambio mínimo: `UsageSources.cs` no se toca; el panel de detalle, el menú
contextual, los timers y la lectura de datos se mantienen.

## Aspecto (el usuario lo quiere grande, nítido y todo junto)
- Dos filas, una por herramienta:
  ```
  [logo Claude]  27   76
  [logo Codex]    9   23
  ```
  Primera columna de números = sesión 5 h; segunda = semanal. Números alineados a la derecha en su
  columna (ancho fijo medido con "100"), separación entre columnas 6 px × k.
- `k = DeviceDpi / 96`. Alto del widget = alto de la barra de tareas − 6 px × k (barra de 48 px → 42 px);
  cada fila ocupa la mitad. Logo de 16 px × k a la izquierda de cada fila, centrado en vertical.
- Números: `Segoe UI`, negrita, 15 px × k (`GraphicsUnit.Pixel`), dibujados con `TextRenderer.DrawText`
  (ClearType) sobre fondo opaco, para que salgan nítidos. Colores por umbral como ahora
  (`UsageColor`: verde < 75, ámbar < 90, rojo ≥ 90). Si una fuente tiene error: "!" gris en lugar de los
  números de esa fila. Sin dato: "--".
- Fondo: rectángulo redondeado (radio 6 px × k, aplica `Region` para recortar las esquinas), color según
  el tema de Windows (`AppsUseLightTheme`, ya se lee en el panel; extrae esa lectura a un método estático
  y úsalo en los dos sitios): oscuro `#202020`, claro `#F3F3F3`. Al pasar el ratón, un tono algo más claro
  (oscuro `#2D2D2D`, claro `#E6E6E6`).
- Logos según tema: oscuro → `Assets\claude.ico` / `Assets\codex.ico` (blancos); claro →
  `Assets\claude-light.ico` / `Assets\codex-light.ico`. Los cuatro como `EmbeddedResource`.
- Ancho = margen 8 px × k + logo + 8 px × k + 2 columnas de números + margen 8 px × k.

## Posición y comportamiento
- Ventana `Form` sin bordes, `ShowInTaskbar = false`, `TopMost = true`, y en `CreateParams.ExStyle`
  `WS_EX_TOOLWINDOW (0x80)` y `WS_EX_NOACTIVATE (0x08000000)`: no aparece en Alt+Tab ni roba el foco.
- Posición: `FindWindow("Shell_TrayWnd", null)` → rectángulo de la barra; dentro,
  `FindWindowEx(tray, 0, "TrayNotifyWnd", null)` → rectángulo del área de notificación.
  `x = notify.Left − ancho − 4 px × k`, `y` centrado en la barra. Si no se encuentran, esquina inferior
  derecha de `Screen.PrimaryScreen.Bounds` encima del área de trabajo.
- Un `System.Windows.Forms.Timer` de 1 s que:
  1. recalcula la posición (la barra puede cambiar: más iconos en la bandeja, cambio de DPI);
  2. reafirma el orden con `SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)`, porque al pulsar la barra esta se pone por encima;
  3. oculta el widget si la ventana en primer plano (`GetForegroundWindow`) ocupa toda la pantalla
     principal (pantalla completa: vídeo, presentación, juego), salvo que su clase sea `Progman`,
     `WorkerW` o `Shell_TrayWnd`; lo vuelve a mostrar cuando deja de estarlo. Si la barra está oculta
     (su `Top` ≥ `Bounds.Bottom` − 2 px, autoocultar), también se oculta.
- Clic izquierdo → abre/cierra el panel de detalle. Coloca el panel encima del widget: borde inferior del
  panel 8 px × k por encima del widget, alineado a la derecha con el widget y sin salir de la pantalla.
- Clic derecho → el `ContextMenuStrip` actual en `Cursor.Position`.
- `ToolTip` con el mismo texto que hoy llevaban los iconos (Claude y Codex, dos líneas).
- Al cambiar los datos: `Invalidate()` del widget (y del panel como ahora).
- Los `NotifyIcon` desaparecen del todo, con su código de dibujo de iconos (`SetNumber`, `DrawIconText`,
  `DestroyIcon`).

## Al terminar
- `dotnet build -c Release` con 0 errores y 0 avisos.
- README: una línea sobre el widget en la barra.
- No arranques la app, no hagas commits, no toques otros ficheros.
