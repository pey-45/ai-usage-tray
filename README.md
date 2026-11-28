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

## Instalación

Descarga y ejecuta `AI-Usage-Tray-1.0.0-Setup.exe` desde las releases.
El instalador coloca la aplicación en `%LOCALAPPDATA%\Programs\AI Usage Tray`,
crea un acceso en el menú Inicio y no requiere permisos de administrador ni instalar .NET.
La opción **Arrancar con Windows** está marcada por defecto y se puede desmarcar durante
la instalación o cambiar después desde el menú de la aplicación.

Para actualizar, cierra la aplicación y ejecuta el nuevo instalador. Conserva la carpeta
y las opciones elegidas en la instalación anterior. Puedes desinstalarla desde la configuración
de aplicaciones de Windows; también se elimina la entrada de inicio automático.

## Generar el instalador

En PowerShell, con el SDK de .NET 10 instalado:

```powershell
.\build-installer.ps1 -Version 1.0.0
```

El script publica la aplicación con .NET incluido y genera
`artifacts\installer\AI-Usage-Tray-1.0.0-Setup.exe`, listo para adjuntar a una release.
Si no encuentra `ISCC.exe` en el PATH, descarga una copia portable de Inno Setup 6.7.4
en `.tools` y verifica su firma antes de ejecutarla. También puedes indicar un compilador
existente con `-IsccPath 'C:\ruta\ISCC.exe'`.

## Requisitos y compilación manual

Windows 10/11, .NET 10 (SDK para compilar, runtime de escritorio para ejecutar), Claude Code con sesión
iniciada y/o Codex CLI.

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

El ejecutable queda en `bin\Release\net10.0-windows\win-x64\publish\UsageTray.exe`.

`docs/` contiene las instrucciones con las que se fue construyendo la app.
