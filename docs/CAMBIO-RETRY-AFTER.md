# Cambio: respetar el Retry-After de Claude

Problema real: `GET https://api.anthropic.com/api/oauth/usage` devuelve `429` con cabecera
`Retry-After` en segundos (visto: `1958`, unos 33 min). La app no lo respeta: el sondeo y, sobre todo,
"Actualizar" a mano vuelven a llamar enseguida, reciben otro 429 y probablemente alargan el bloqueo.

Cambio mínimo, sin abstracciones nuevas:

1. `UsageSources.cs`
   - `UsageReading` gana un campo opcional `DateTimeOffset? RetryAt = null` al final.
   - En `RequestClaudeUsageAsync`, antes de `EnsureSuccessStatusCode`: si el estado es 429, devolver
     `new(Error: $"limitado hasta {hora local HH:mm}", RetryAt: ...)`. `RetryAt` = ahora + `Retry-After`
     (`response.Headers.RetryAfter.Delta`, o `.Date` si viene como fecha); si no viene la cabecera,
     ahora + 15 min. Que esa lectura no se reintente tras refrescar credenciales (el reintento actual
     es solo para 401).
   - Ojo: con el token caducado el endpoint también contesta 429, así que el refresco previo por
     `expiresAt` debe seguir ocurriendo antes de la petición (ya es así; no lo rompas).

2. `Program.cs` (`RefreshAllAsync`)
   - Nuevo campo `_claudeBlockedUntil` (`DateTimeOffset.MinValue` al inicio).
   - Se consulta Claude solo si `ahora >= _claudeBlockedUntil` **y** (`manual` o `ahora >= _claudeDue`).
     Es decir, actualizar a mano salta el intervalo de 5 min pero **nunca** el bloqueo del servidor;
     en ese caso solo se refresca Codex.
   - Si la lectura trae `RetryAt`: `_claudeBlockedUntil = _claudeDue = RetryAt` (sin tocar la espera
     exponencial). Si no, la lógica actual (5 min, doblando hasta 15 si falla).
   - El mensaje que ya se muestra en panel y tooltip queda como
     "sin actualizar desde HH:mm: limitado hasta HH:mm".

Al terminar: `dotnet build -c Release` con 0 errores y 0 avisos. No arranques la app, no hagas commits
ni toques otros ficheros.
