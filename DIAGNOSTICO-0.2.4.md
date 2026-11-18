# Arideev Suno Presence 0.2.4 — diagnóstico de conexión local

## Causa más probable del bloqueo en Chrome/Edge actuales

La extensión 0.2.3 enviaba las canciones desde el service worker Manifest V3 a `http://127.0.0.1:47831`.
Chromium introdujo Local Network Access (LNA): las peticiones a loopback ahora requieren permiso y la primera solicitud que dispara el permiso debe salir de un documento activo mediante una acción explícita del usuario. Un service worker no tiene un documento visible para mostrar ese permiso.

La versión 0.2.4 añade un popup de extensión. Al pulsar **Conectar con Arideev**, el popup hace un `GET /health` desde un documento de extensión y dentro del gesto del usuario. Después de conceder el permiso, el service worker puede continuar con los POST de pistas. También se marca `targetAddressSpace: "loopback"` en las peticiones.

Chrome 145 separó el permiso en `loopback-network` (127.0.0.1 / localhost) y `local-network` (direcciones privadas de la LAN). El proyecto utiliza únicamente loopback, no necesita acceso a toda la red local.

## Pasos de actualización

1. Compila/publica la aplicación 0.2.4 y ejecútala.
2. En `chrome://extensions` y `edge://extensions`, pulsa **Recargar** en Arideev Suno Presence Connector.
3. Haz clic en el icono de la extensión en la barra del navegador.
4. Pulsa **Conectar con Arideev**.
5. Si el navegador solicita permiso de red local / loopback, selecciona **Permitir**.
6. Abre o recarga Suno con `Ctrl+R`.
7. Reproduce una canción.
8. En la app, comprueba `BrowserDiagnostics` y usa **Probar conexión con Discord** de forma independiente.

El texto `service worker (Inactiva)` en la página de extensiones es normal en Manifest V3: Chromium suspende los service workers cuando no tienen trabajo.

## Interpretar los resultados

- Popup: **Conexión local y service worker verificados** — tanto el documento del popup como el service worker llegaron a la app.
- Popup: **El popup llega a Arideev, pero el service worker no puede enviar datos** — el endpoint responde, pero el proceso MV3 no ha obtenido acceso; revisa permisos de red local/loopback y vuelve a pulsar Conectar.
- App: **Extensión conectada · esperando una pista reproducible** — el puente ya recibe heartbeat/prueba; el diagnóstico muestra si Media Session o el elemento de audio están disponibles.
- App: `MediaSession=playing` y `reproduciendo=1` — Suno informa una reproducción activa; debe aparecer la pista.
- App: `MediaSession=none; audio=0; vídeo=0` — la vista actual de Suno no expone reproductor/Media Session al detector; no es un fallo de Discord RPC.
- Prueba Discord correcta pero sin canción — la conexión RPC funciona y el problema está en la detección de Suno.
- Prueba Discord fallida — revisar que Discord Desktop esté abierto y que el Application ID pertenezca a la aplicación de Rich Presence.

## Seguridad

El puente solo escucha en `127.0.0.1`; no queda expuesto a otros dispositivos de la red. `/health` es de solo lectura. `/track` exige un origen `chrome-extension://` con formato de ID Chromium y `application/json`.
