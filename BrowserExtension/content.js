(() => {
  "use strict";
  const SOURCE = "ARIDEEV_SUNO_PRESENCE_V3";
  let lastSignature = "";
  let lastSentAt = 0;

  const sendToApp = payload => {
    chrome.runtime.sendMessage({ type: "ARIDEEV_TRACK", payload })
      .then(response => {
        if (!response?.ok) console.warn("[Arideev Suno Presence] Puente local rechazó el envío:", response?.error || "sin respuesta");
      })
      .catch(error => console.warn("[Arideev Suno Presence] No se pudo contactar con la aplicación:", String(error)));
  };

  window.addEventListener("message", event => {
    if (event.source !== window || event.origin !== location.origin || !event.data) return;

    if (event.data.source === SOURCE + "_ERROR") {
      sendToApp({
        hasTrack: false,
        source: "suno",
        diagnosticMessage: "Error en el detector de página: " + String(event.data.message || "error desconocido").slice(0, 180),
        pageUrl: location.href
      });
      return;
    }

    if (event.data.source !== SOURCE) return;
    const payload = event.data.payload;
    if (!payload || typeof payload !== "object") return;

    let songPageUrl = location.href;
    try {
      const candidate = new URL(String(payload.pageUrl || location.href), location.href);
      if (candidate.protocol === "https:" && /(^|\.)suno\.com$/i.test(candidate.hostname))
        songPageUrl = candidate.href;
    } catch { /* Keep the current Suno tab URL. */ }

    const safe = {
      hasTrack: !!payload.hasTrack,
      title: String(payload.title || "Canción desconocida").slice(0, 120),
      artist: String(payload.artist || "Artista desconocido").slice(0, 100),
      coverUrl: typeof payload.coverUrl === "string" ? payload.coverUrl.slice(0, 2048) : null,
      trackId: String(payload.trackId || "").slice(0, 300),
      pageUrl: songPageUrl,
      durationSeconds: Number(payload.durationSeconds) || 0,
      positionSeconds: Number(payload.positionSeconds) || 0,
      isPlaying: !!payload.isPlaying,
      source: "suno",
      mediaSessionAvailable: !!payload.mediaSessionAvailable,
      mediaSessionHasMetadata: !!payload.mediaSessionHasMetadata,
      mediaSessionState: String(payload.mediaSessionState || "unknown").slice(0, 24),
      audioElementCount: Number(payload.audioElementCount) || 0,
      videoElementCount: Number(payload.videoElementCount) || 0,
      playingMediaCount: Number(payload.playingMediaCount) || 0,
      diagnosticMessage: String(payload.diagnosticMessage || "").slice(0, 240)
    };

    // Keep reporting a lightweight heartbeat while Suno is open so the desktop app
    // can distinguish "extension is alive but player isn't exposed" from "no bridge".
    const signature = JSON.stringify({ ...safe, positionSeconds: 0 });
    const now = Date.now();
    if (signature === lastSignature && now - lastSentAt < 1500) return;
    lastSignature = signature;
    lastSentAt = now;
    sendToApp(safe);
  });
})();
