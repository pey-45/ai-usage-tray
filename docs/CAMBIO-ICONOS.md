# Cambio: iconos nítidos con logo (opción B)

Modifica la app existente (`Program.cs`, `UsageTray.csproj`). Mismas reglas de diseño que en
INSTRUCCIONES.md: cambio mínimo, sin abstracciones nuevas, sin NuGet. `UsageSources.cs` no se toca.

## Qué cambia
1. **Cuatro `NotifyIcon` en vez de dos**, creados en este orden: logo Claude, números Claude, logo Codex,
   números Codex.
   - Los logos salen de `Assets\claude.ico` y `Assets\codex.ico` (ya están en la carpeta). Inclúyelos como
     `EmbeddedResource` en el csproj y cárgalos con `new Icon(stream, SystemInformation.SmallIconSize)`.
     El logo no cambia nunca.
   - Los dos iconos de cada herramienta comparten tooltip, clic izquierdo (panel) y menú contextual.
2. **Números nítidos.** Sustituir el dibujo con `GraphicsPath` + escalado (sale pixelado) por texto con
   hinting:
   - `graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit` sobre el bitmap transparente.
   - Fuente `Segoe UI` negrita, `GraphicsUnit.Pixel`, tamaño 8 px × (alto del icono / 16).
   - `StringFormat` centrado en horizontal y vertical. Línea de arriba (5 h) en el rectángulo
     `(0, -1·k, ancho, 9·k)` y la de abajo (semanal) en `(0, 7·k, ancho, 9·k)`, con `k = alto / 16`.
   - Valores de 3 cifras ("100"): bajar la fuente a 6,5 px × k para que quepa.
   - Si hay error: un "!" gris claro centrado ocupando todo el icono, misma técnica.
   - Mantener `DestroyIcon` del HICON y el `Dispose` del icono anterior.
3. **Colores por umbral** (iconos y barras del panel, sustituye al color de marca en ambos sitios):
   verde `#5DCA5A` por debajo de 75 %, ámbar `#EFB027` de 75 a 89,99 %, rojo `#F05A5A` desde 90 %.
   El color de marca solo se queda, si quieres, en el título de cada sección del panel; nada más.

## Al terminar
- `dotnet build -c Release` sin errores ni avisos.
- Actualiza el README en una línea si hace falta (4 iconos).
- No arranques la app, no hagas commits ni toques otros ficheros.
