<p align="center">
  <img src="Assets/Arideev.png" alt="aarideev" width="112">
</p>

<h1 align="center">aarideev&nbsp; | &nbsp;SunoSync</h1>

<p align="center">
  Tu música de Suno, sincronizada con tu presencia de Discord.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-4C8BF5?logo=windows" alt="Windows 10 y 11">
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet" alt=".NET 10">
  <img src="https://img.shields.io/badge/Brave%20%7C%20Chrome%20%7C%20Edge-MV3-FB542B?logo=googlechrome" alt="Brave, Chrome y Edge">
  <img src="https://img.shields.io/badge/Release-v0.3.12-E34A5E" alt="Release 0.3.12">
</p>

---

## ✦ Tu música, siempre presente

SunoSync detecta lo que estás escuchando en Suno y lo muestra en tu perfil de Discord. La app se queda discretamente en la bandeja, mantiene la conexión y puede actualizarse desde GitHub Releases.

| En tiempo real | Ligera y discreta | Hecha para ti |
|:--|:--|:--|
| Canción, artista, portada y reproducción | Se integra con Discord Desktop | Icono y estilo aarideev |
| Brave, Chrome y Edge | Inicio opcional con Windows | Actualizaciones automáticas |

---

## ◆ Primeros pasos

### 1. Instala SunoSync

Descarga el instalador más reciente desde [Releases](https://github.com/aariidev/Sndspresence/releases/latest) y ejecútalo.

### 2. Conecta Discord

1. Crea una aplicación en el [Discord Developer Portal](https://discord.com/developers/applications).
2. Copia su **Application ID** —no un bot token— y pégalo en SunoSync.
3. Deja activada la opción **Rich Presence** y pulsa **Guardar configuración**.
4. Comprueba la conexión con **Probar conexión con Discord**. Discord Desktop debe estar abierto.

### 3. Conecta tu navegador

1. Abre `brave://extensions`, `chrome://extensions` o `edge://extensions`.
2. Activa **Modo de desarrollador** y carga como descomprimida la carpeta indicada por SunoSync.
3. Abre el icono de **aarideev | SunoSync** y pulsa **Conectar con SunoSync**.
4. Permite el acceso local si el navegador te lo solicita.
5. Recarga Suno, inicia una canción y listo.

> **¿No aparece la canción?** Comprueba los paneles de estado de SunoSync. El popup verifica por separado el permiso local y el puente de la extensión.

---

## ◈ Actualizaciones

SunoSync consulta GitHub Releases al iniciar y descarga la nueva versión en segundo plano. La descarga se verifica con SHA-256. Cuando esté lista, selecciona **Salir** desde el icono de la bandeja: el instalador se ejecutará en modo silencioso y SunoSync volverá a abrirse minimizado.

La app actualiza los archivos de la extensión junto con cada instalación. Como la extensión se carga localmente, después de actualizar puede ser necesario pulsar **Recargar** en la página de extensiones de Brave, Chrome o Edge.

---

## ⚙ Compilar desde el código

**Necesitas:** Windows 10/11 x64, .NET 10 SDK y [Inno Setup 6](https://jrsoftware.org/isinfo.php) para crear el instalador.

```powershell
.\Scripts\Build-Windows.ps1
```

Para generar solo la app autónoma:

```powershell
.\Scripts\Build-Windows.ps1 -SkipInstaller
```

El ejecutable aparece en `publish\ArideevSunoPresence.exe`; el instalador, en `dist\`.

### Publicar una versión

Actualiza el mismo número de versión en el proyecto y en el manifiesto de la extensión. Después, crea y sube una etiqueta `vX.Y.Z`; GitHub Actions compilará y publicará el instalador y su checksum SHA-256.

```powershell
git tag v0.3.12
git push origin v0.3.12
```

---

## ◇ Cómo funciona

```text
Suno en el navegador → Extensión MV3 → Puente local (127.0.0.1) → SunoSync → Discord Desktop
```

El puente solo escucha en loopback. La extensión envía los metadatos de reproducción; SunoSync se comunica con Discord Desktop mediante Discord IPC local.

---

## Límites conocidos

- Los cambios en el reproductor o en Media Session de Suno pueden afectar la detección.
- La extensión instalada como descomprimida no se recarga sola después de actualizar la app.
- La portada de Discord depende de que la imagen sea accesible públicamente.

---

<p align="center">
  <sub>Hecho con ♥ por aarideev · SunoSync 0.3.12</sub>
</p>
