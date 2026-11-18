# Arideev Suno Presence 0.2.5 — hotfix de compilación

Esta revisión corrige un error de sintaxis C# en el endpoint `GET /health` de `Services/LocalBridge.cs`.

En 0.2.4, el JSON de respuesta se escribió con escape doble de comillas (`\\"`) dentro de una cadena C#, lo que provocaba una cascada de errores CS1003 y CS1056 en la línea 111.

En 0.2.5, la respuesta se construye con `JsonSerializer.Serialize` usando un objeto anónimo, evitando el escape manual. También se actualizó el número de versión en el proyecto, la extensión, la interfaz y el instalador.

No se ha cambiado el protocolo de conexión ni la detección de Suno.
