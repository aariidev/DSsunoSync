# Arideev Suno Presence — Alpha 0.3.0

Aplicación Windows WPF + extensión Chromium MV3 para Brave, Chrome y Edge; detecta la canción de Suno y publica Rich Presence en Discord.

## Importante: permiso de conexión local (Brave/Chromium)

Chromium añadió Local Network Access. Las peticiones a `127.0.0.1` requieren autorización y la primera solicitud que activa el permiso no debe originarse en un service worker MV3. Por eso esta versión incluye un popup de extensión:

1. Abre Arideev Suno Presence.
2. Recarga la extensión en `brave://extensions`, `chrome://extensions` o `edge://extensions`.
3. Pulsa el icono de la extensión en la barra del navegador.
4. Pulsa **Conectar con Arideev**.
5. Permite el acceso local / loopback si el navegador lo solicita.
6. Espera al mensaje que confirma tanto el popup como el service worker.
7. Recarga Suno y reproduce una canción.

`service worker (Inactiva)` es el comportamiento normal de Manifest V3 cuando el worker no está ejecutando una tarea.

## Requisitos de compilación

- Windows 10/11 x64
- .NET 10 SDK 10.0.100 o posterior
- Discord Desktop
- Inno Setup 6 (solo para generar el instalador)

## Compilar

Ejecuta `Build-Windows.bat`. También puedes ejecutar:

```powershell
.\Scripts\Build-Windows.ps1
```

Para publicar solo el EXE sin intentar crear instalador:

```powershell
.\Scripts\Build-Windows.ps1 -SkipInstaller
```

El EXE autónomo aparece en `publish\ArideevSunoPresence.exe`.
El instalador Inno Setup aparece en `dist\ArideevSunoPresence-Setup-x64-0.3.0.exe`.

## Iniciar con Windows

En la aplicación, activa **Iniciar con Windows (en la bandeja)** y pulsa **Guardar configuración**. Se iniciará al iniciar sesión en tu cuenta de Windows y quedará minimizada en la bandeja del sistema. Desmarca la opción y vuelve a guardar para quitarla del inicio.

## Instalación de la extensión

La app extrae la extensión a `%APPDATA%\Arideev\SunoPresence\BrowserExtension`. Carga esa carpeta una vez en Brave, Chrome o Edge mediante la opción **Cargar descomprimida**. Tras actualizar la app, vuelve a la página de extensiones y pulsa **Recargar**.

## Diagnóstico independiente

La app dispone de:
- `GET http://127.0.0.1:47831/health` para comprobar que el puente local está vivo.
- Panel `BrowserDiagnostics` para inspeccionar Media Session, audio y vídeo.
- Botón **Probar conexión con Discord** para aislar fallos del RPC del detector de Suno.

Consulta `DIAGNOSTICO-0.2.5.md` para interpretar los estados.

## Actualizaciones

La app instalada consulta las versiones publicadas en GitHub Releases y descarga el instalador más reciente en segundo plano. Al elegir **Salir** desde el icono de la bandeja, la nueva versión se instala y la app vuelve a abrirse minimizada en la bandeja. La descarga se verifica con SHA-256 antes de ejecutarse.

El workflow `.github/workflows/release-windows.yml` genera y publica el instalador al subir una etiqueta `vX.Y.Z` que coincida con la versión del proyecto. El repositorio debe tener el código y una primera release publicada para que el actualizador pueda encontrar versiones.

Cada instalador incluye la extensión actualizada. Como la extensión se carga como descomprimida, puede ser necesario pulsar **Recargar** en `brave://extensions`, `chrome://extensions` o `edge://extensions` después de actualizar.

## Limitaciones conocidas

- Suno puede cambiar la estructura de su reproductor o dejar de exponer metadatos a Media Session; los selectores/heurísticas necesitarán ajustes cuando eso ocurra.
- La carátula original solo se mostrará si Suno publica una URL HTTPS accesible que Discord acepte como imagen externa.
- La compilación final debe ejecutarse en Windows con el .NET 10 SDK instalado. Este paquete contiene código fuente y scripts, no binarios compilados.


## Hotfix 0.2.5

Corrige el escape inválido del JSON en `GET /health` del puente local que impedía compilar `LocalBridge.cs` en 0.2.4. Consulta `DIAGNOSTICO-0.2.5.md`.
