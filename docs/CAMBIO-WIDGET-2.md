# Cambio 2 del widget: sin fondo, sin parpadeos, refresco manual y cada minuto

Sobre el código actual (`Program.cs`, y `UsageSources.cs` solo si hace falta para el punto 4). Mismas
reglas: cambio mínimo, sin abstracciones nuevas, sin NuGet.

## 1. Quitar el fondo (transparente de verdad)
- El widget debe verse como si los logos y los números estuvieran pintados sobre la barra de tareas,
  sin rectángulo detrás.
- Usa una ventana con transparencia por píxel: `WS_EX_LAYERED (0x80000)` en `CreateParams.ExStyle` y
  pinta con `UpdateLayeredWindow` (+ `ULW_ALPHA`, `BLENDFUNCTION` con `AC_SRC_ALPHA`) a partir de un
  `Bitmap` 32 bpp ARGB. Elimina `OnPaint`, `Region` y el rectángulo redondeado: ya no hacen falta.
  Cada vez que cambien datos, tamaño, DPI, tema u hover, se regenera el bitmap y se vuelve a llamar a
  `UpdateLayeredWindow` (un método `Render()`; `UpdateReading`/`UpdatePosition` lo llaman en vez de
  `Invalidate`).
- Texto con GDI+ (`Graphics.DrawString`, `TextRenderingHint.AntiAliasGridFit`): ClearType no funciona
  sobre fondo transparente. Misma fuente (Segoe UI negrita 15 px × k) y colores por umbral. Para que se
  lea sobre cualquier barra, dibuja debajo de cada número un contorno/sombra suave de 1 px × k
  (negro al 60 % de alfa en tema oscuro, blanco al 60 % en tema claro), p. ej. dibujando el texto
  desplazado o con `GraphicsPath` + `Pen`. Logos igual que ahora (según tema).
- Para que toda la caja siga recibiendo clics y hover, el fondo no es alfa 0 sino alfa 1 (invisible).
  Con hover, fondo blanco al 8 % de alfa (tema oscuro) o negro al 6 % (tema claro), con esquinas
  redondeadas de 6 px × k.
- Recuerda: con `UpdateLayeredWindow` la posición y el tamaño también se pasan en esa llamada; mantén
  la lógica de colocación actual.

## 2. Bug: a veces desaparece unos segundos al pulsar fuera
Causa probable: al pulsar en la barra de tareas o en otra ventana, la barra se pone por encima del
widget y el timer de 1 s tarda en devolverlo arriba; además la comprobación de pantalla completa puede
ocultarlo por error. Arréglalo así:
- `SetWinEventHook` (fuera de proceso, `WINEVENT_OUTOFCONTEXT`) para `EVENT_SYSTEM_FOREGROUND (0x0003)`
  y para `EVENT_OBJECT_REORDER (0x8004)`; en el callback, reafirma el `HWND_TOPMOST` al momento
  (y recalcula visibilidad). Guarda el delegado en un campo para que el GC no lo recoja y
  `UnhookWinEvent` en `Dispose`.
- Deja el timer como red de seguridad pero a 500 ms.
- Pantalla completa: solo cuenta si la ventana en primer plano cubre exactamente toda la pantalla
  principal **y** no es maximizada (`IsZoomed` = false) **y** es visible y no está "cloaked"
  (`DwmGetWindowAttribute(DWMWA_CLOAKED = 14)` ≠ 0 → ignorar). Excluye también nuestras propias ventanas
  (widget, panel, menú: mismo proceso → `GetWindowThreadProcessId` = nuestro PID) y las clases
  `Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `NotifyIconOverflowWindow`,
  `TopLevelWindowForOverflowXamlIsland`, `Windows.UI.Core.CoreWindow`, `XamlExplorerHostIslandWindow`.
- Para ocultar por pantalla completa o barra autooculta, exige que la condición se mantenga en dos
  comprobaciones seguidas, para no parpadear por estados transitorios.

## 3. Actualizar a mano
- Doble clic izquierdo en el widget → actualizar ahora (Claude y Codex). El clic simple sigue abriendo
  el panel, pero espera a `SystemInformation.DoubleClickTime` antes de abrirlo para no abrirlo en un
  doble clic.
- En el panel, abajo a la izquierda en lugar de "Actualizado hh:mm", un enlace "Actualizar · hh:mm"
  (`LinkLabel` o zona clicable dibujada) que actualiza ahora.
- Mientras se actualiza, en el panel "Actualizando…" y en el widget los números al 50 % de opacidad.
- El menú contextual conserva "Actualizar ahora".

## 4. Sondeo cada minuto
- Un único timer de 60 s para Claude y Codex (elimina `_codexTimer`).
- Si una lectura falla y ya había una buena, **se siguen mostrando los últimos valores buenos**; el
  error va al tooltip y al panel ("sin actualizar desde hh:mm: motivo"). El "!" solo aparece si nunca
  hubo una lectura buena. Si Claude responde 429, trátalo como un fallo más (sin "!").

## Al terminar
- `dotnet build -c Release` con 0 errores y 0 avisos.
- No arranques la app, no hagas commits, no toques otros ficheros salvo `README.md` (una línea sobre el
  doble clic y el refresco cada minuto).
