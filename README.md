# AI Usage Tray

Muestra en la barra de tareas de Windows cuánto llevas gastado de los límites de **Claude** y **Codex**
(ventana de 5 horas y semanal), en un recuadro transparente a la izquierda de los iconos de la bandeja:

```
[logo Claude]  31  77
[logo Codex]   29  26
```

Primera columna: sesión de 5 h. Segunda: semanal. Verde < 75 %, ámbar < 90 %, rojo ≥ 90 %.

- **Clic izquierdo:** panel con el detalle y la hora de reinicio de cada límite.
- **Doble clic** o enlace **Actualizar** del panel: refresca al momento.
- **Clic derecho:** actualizar, arrancar con Windows, salir.
- Se oculta con aplicaciones a pantalla completa y si la barra está autooculta.

## De dónde salen los datos

- **Claude:** `GET https://api.anthropic.com/api/oauth/usage` con el token OAuth de Claude Code
  (`%USERPROFILE%\.claude\.credentials.json`). La app nunca escribe ese fichero: si el token ha caducado
  ejecuta `claude auth status`, que lo renueva. El endpoint **no es oficial** y puede cambiar; limita las
  peticiones, así que se consulta cada 5 min (hasta 15 min si devuelve error).
- **Codex:** último evento `rate_limits` de `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`. Sin red;
  se relee cada minuto y solo cambia cuando usas Codex.
- **Logos:** se leen de las apps de Claude y Codex instaladas desde la Microsoft Store (no se distribuyen
  aquí). Si no están, se muestra «Cl» / «Cx».

## Requisitos y compilación

Windows 10/11, .NET 10 (SDK para compilar, runtime de escritorio para ejecutar), Claude Code con sesión
iniciada y/o Codex CLI.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

El ejecutable queda en `bin\Release\net10.0-windows\win-x64\publish\UsageTray.exe`.

`docs/` contiene las instrucciones con las que se fue construyendo la app.
