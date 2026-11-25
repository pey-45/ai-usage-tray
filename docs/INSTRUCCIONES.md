# UsageTray — instrucciones de implementación

Crea en esta carpeta una app de bandeja de Windows (C#, WinForms, `net10.0-windows`, SDK 10 instalado)
que muestre el uso de Claude y de Codex: ventana de 5 h y semanal.

## Reglas de diseño (obligatorias)
- Simple, sin sobreingeniería: sin interfaces, sin DI, sin patrones, sin NuGet. Solo BCL
  (`System.Text.Json`, `HttpClient`, WinForms, GDI+).
- Pocos ficheros: `UsageTray.csproj`, `Program.cs` (bandeja + panel), `UsageSources.cs` (lectura de datos),
  `README.md` corto en español. Nada más salvo que sea imprescindible.
- `OutputType` WinExe, `UseWindowsForms` true, `Nullable` enable, `ApplicationHighDpiMode` PerMonitorV2.
- Textos de la UI en español.
- Nunca escribir en log, pantalla ni fichero el token de acceso.

## Fuente 1: Claude
1. Leer `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken` y `claudeAiOauth.expiresAt`
   (ms epoch). Solo lectura: la app NO escribe nunca este fichero.
2. Si `expiresAt` ya pasó, o la petición devuelve 401: ejecutar oculto (`CreateNoWindow`, sin shell)
   `claude auth status` (está en el PATH; si no, `%USERPROFILE%\.local\bin\claude.exe`), esperar a que
   termine (timeout 30 s), volver a leer el fichero y reintentar una sola vez. Ese comando renueva el token
   y lo guarda él mismo.
3. `GET https://api.anthropic.com/api/oauth/usage` con cabeceras
   `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`, `User-Agent: usage-tray`.
   Respuesta real (recortada):
   ```json
   {"five_hour":{"utilization":8.0,"resets_at":"2026-11-25T13:09:59.705852+00:00"},
    "seven_day":{"utilization":73.0,"resets_at":"2026-11-26T08:59:59.705871+00:00"}, ...}
   ```
   `utilization` ya es porcentaje (0–100). Cualquiera de los dos puede ser `null`: entonces se muestra "--".
   Ignorar el resto de campos.

## Fuente 2: Codex (sin red)
- Buscar en `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl` el fichero modificado más recientemente
  (las carpetas son `AAAA\MM\DD`; basta con mirar las carpetas de los últimos días, no recorrer todo).
- Leerlo con `FileShare.ReadWrite` (Codex puede estar escribiendo) y quedarse con la ÚLTIMA línea que
  contenga `"rate_limits"`. Formato real:
  ```json
  {"timestamp":"2026-11-25T08:20:19.907Z","type":"event_msg","payload":{"type":"token_count","info":{...},
   "rate_limits":{"limit_id":"codex","primary":{"used_percent":1.0,"window_minutes":300,"resets_at":1795612755},
   "secondary":{"used_percent":22.0,"window_minutes":10080,"resets_at":1795866788},"credits":{...}}}}
  ```
  `primary` = 5 h, `secondary` = semanal, `resets_at` en segundos epoch.
- Si el `resets_at` de una ventana ya pasó, esa ventana se ha reiniciado: mostrar 0 %.
- Guardar el `timestamp` de la línea para mostrar "dato de hace X" en el panel (Codex solo se actualiza
  cuando se usa).

## Bandeja
- Dos `NotifyIcon`: uno para Claude y otro para Codex.
- Cada icono se dibuja en tiempo de ejecución (16×16 escalado al DPI del sistema; usar
  `SystemInformation.SmallIconSize`): dos números apilados, arriba el % de 5 h y abajo el semanal, sin
  el signo %. "100" cabe reduciendo la fuente. Color de marca: Claude naranja `#D85A30`,
  Codex verde `#1D9E75`. Cada número cambia a ámbar `#EF9F27` desde el 75 % y a rojo `#E24B4A` desde el 90 %.
  Fondo transparente, texto con buen contraste en la barra oscura de Windows 11. Liberar los `HICON` creados
  (`DestroyIcon`) para no perder handles.
- Tooltip (`Text`, máx. 127 caracteres): `Claude · 5 h 8 % · semana 73 %`.
- Si una fuente falla: el icono muestra "!" en gris y el tooltip el motivo corto.

## Panel
- Clic izquierdo en cualquiera de los dos iconos: abre un formulario pequeño sin bordes, encima del reloj
  (abajo a la derecha de `Screen.PrimaryScreen.WorkingArea`), que se cierra al perder el foco.
- Contenido: sección "Claude" y sección "Codex". Cada una con dos filas ("Sesión 5 h" y "Semanal"),
  cada fila con el %, la hora de reinicio en hora local ("reinicio 17:40" si es hoy, "reinicio jue 09:00"
  si no) y una barra de progreso dibujada (no `ProgressBar`) con el mismo código de colores. Al pie,
  "Actualizado hh:mm" y, para Codex, "dato de hace X min".
- Respetar el tema claro/oscuro de Windows
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`).

## Menú (clic derecho)
- "Actualizar ahora", "Arrancar con Windows" (casilla; añade o quita el valor `UsageTray` en
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` con la ruta del exe), "Salir".

## Refresco
- Al arrancar y luego con un `System.Windows.Forms.Timer` cada 3 minutos. Claude con `HttpClient` estático
  (timeout 15 s) y `async/await`, sin bloquear la UI. Codex además se relee cada 30 s (es local y barato).
- Una sola instancia (`Mutex` con nombre); si ya hay una abierta, salir sin hacer nada.

## Al terminar
- `dotnet build -c Release` sin errores ni avisos.
- `README.md`: qué hace, cómo compilar
  (`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`), dónde queda el
  exe y que el endpoint de Claude no es oficial.
- No crear repositorio git ni hacer commits.
