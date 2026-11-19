(() => {
  "use strict";
  if (window.__arideevSunoBridgeV3) return;
  window.__arideevSunoBridgeV3 = true;

  const SOURCE = "ARIDEEV_SUNO_PRESENCE_V3";
  const clean = value => String(value ?? "").replace(/\s+/g, " ").trim();

  const first = (selectors, root = document) => {
    for (const selector of selectors) {
      const node = root.querySelector?.(selector);
      if (!node) continue;
      const value = clean(
        node.getAttribute?.("content")
        || node.getAttribute?.("aria-label")
        || node.innerText
        || node.textContent
      );
      if (value) return value;
    }
    return "";
  };

  // Suno has changed its internal player markup several times. Search ordinary DOM
  // and open shadow roots rather than depending on one CSS class or test ID.
  const collectMediaElements = () => {
    const found = [];
    const visited = new Set();
    const roots = [document];
    while (roots.length) {
      const root = roots.pop();
      if (!root || visited.has(root)) continue;
      visited.add(root);
      try {
        found.push(...root.querySelectorAll("audio,video"));
        for (const element of root.querySelectorAll("*")) {
          if (element.shadowRoot) roots.push(element.shadowRoot);
        }
      } catch { /* An inaccessible/closed shadow root is simply skipped. */ }
    }
    return [...new Set(found)];
  };

  let lastActiveMedia = null;

  const readMediaSession = () => {
    try {
      const session = navigator.mediaSession;
      const metadata = session?.metadata || null;
      return {
        available: !!session,
        metadata,
        state: String(session?.playbackState || "none"),
        hasMetadata: !!(metadata?.title || metadata?.artist || metadata?.album)
      };
    } catch {
      return { available: false, metadata: null, state: "unavailable", hasMetadata: false };
    }
  };

  const getArtwork = (metadata, playerRoot) => {
    const artwork = metadata?.artwork;
    if (Array.isArray(artwork) && artwork.length) {
      const sorted = [...artwork].sort((a, b) => {
        const size = value => {
          const match = String(value || "").match(/(\d+)\s*x\s*(\d+)/i);
          return match ? Number(match[1]) * Number(match[2]) : 0;
        };
        return size(b.sizes) - size(a.sizes);
      });
      if (sorted[0]?.src) return sorted[0].src;
    }
    const roots = playerRoot && playerRoot !== document ? [playerRoot, document] : [document];
    for (const root of roots) {
      const image = root.querySelector?.(
        'img[src], img[data-src], img[alt*="cover" i], img[alt*="artwork" i], img[alt*="song" i]'
      );
      const candidate = image?.currentSrc || image?.src || image?.getAttribute?.("data-src");
      if (candidate) return candidate;
    }
    return document.querySelector('meta[property="og:image"]')?.content || null;
  };

  const getPlayerRoot = media => {
    let node = media;
    for (let i = 0; i < 8 && node?.parentElement; i++) {
      node = node.parentElement;
      if (node.querySelectorAll?.("img").length && node.querySelectorAll?.("button").length)
        return node;
    }
    return document;
  };

  const findCreator = root => {
    const anchors = [...(root.querySelectorAll?.('a[href*="/@"], a[href*="/profile/"]') || [])];
    const anchor = anchors.find(item => clean(item.innerText || item.getAttribute("aria-label")) || item.getAttribute("href"));
    const href = anchor?.getAttribute("href") || "";
    const fromHref = href.match(/\/@([^/?#]+)/)?.[1] || "";
    return clean(anchor?.innerText || anchor?.getAttribute("aria-label") || fromHref);
  };

  const extract = () => {
    const session = readMediaSession();
    const mediaElements = collectMediaElements();
    const audioElements = mediaElements.filter(item => item instanceof HTMLAudioElement);
    const videoElements = mediaElements.filter(item => item instanceof HTMLVideoElement);
    const playingMedia = mediaElements.filter(item => !item.paused && !item.ended);
    if (playingMedia.length) lastActiveMedia = playingMedia[0];

    const progressedMedia = mediaElements
      .filter(item => !item.ended && Number(item.currentTime) > 0.12 && (item.currentSrc || item.src))
      .sort((a, b) => Number(b.currentTime) - Number(a.currentTime))[0] || null;

    let activeMedia = playingMedia[0] || null;
    if (!activeMedia && lastActiveMedia && mediaElements.includes(lastActiveMedia)
      && !lastActiveMedia.ended && (lastActiveMedia.currentSrc || lastActiveMedia.src)) {
      activeMedia = lastActiveMedia;
    }
    if (!activeMedia && progressedMedia) {
      activeMedia = progressedMedia;
      lastActiveMedia = progressedMedia;
    }
    if (!activeMedia && session.hasMetadata && session.state === "paused") {
      activeMedia = mediaElements.find(item => !item.ended && (item.currentSrc || item.src)) || null;
    }

    const playerRoot = getPlayerRoot(activeMedia);
    const titleSelectors = [
      '[data-testid="song-title"]', '[data-testid="track-title"]',
      '[data-testid*="song-title"]', '[data-testid*="track-title"]',
      '[class*="song-title"]', '[class*="track-title"]',
      '[class*="SongTitle"]', '[class*="TrackTitle"]'
    ];

    const domTitle = first(titleSelectors, playerRoot)
      || (activeMedia ? first(["main h1", "main h2"]) : "");
    const pageTitle = clean(document.querySelector('meta[property="og:title"]')?.content)
      || clean(document.title.replace(/\s*[|·-]\s*Suno.*$/i, ""));
    const rawTitle = clean(session.metadata?.title) || domTitle || pageTitle;
    const titleLooksGeneric = !rawTitle || /^(suno|suno ai|create|home|discover|library|studio)$/i.test(rawTitle);

    const artistSelectors = [
      '[data-testid*="artist"]', '[data-testid*="creator"]',
      '[class*="artist-name"]', '[class*="creator-name"]',
      '[class*="ArtistName"]', '[class*="CreatorName"]'
    ];
    const creator = findCreator(playerRoot) || findCreator(document);
    const artist = clean(session.metadata?.artist)
      || first(artistSelectors, playerRoot)
      || creator
      || first(['meta[name="author"]'])
      || "Artista desconocido";
    const album = clean(session.metadata?.album)
      || first(['[data-testid*="album"]', '[class*="album-name"]', '[class*="AlbumName"]'], playerRoot);

    const songAnchor = (playerRoot !== document ? playerRoot.querySelector('a[href*="/song/"]') : null)
      || document.querySelector('a[href*="/song/"]');
    const songUrl = songAnchor?.href ? new URL(songAnchor.href, location.origin).href : location.href;

    const knownPausedMedia = !!(
      activeMedia && activeMedia.paused && !activeMedia.ended
      && (activeMedia.currentSrc || activeMedia.src)
      && (activeMedia.currentTime > 0.1 || session.state === "paused" || lastActiveMedia === activeMedia)
    );
    const sessionTrack = session.hasMetadata && (session.state === "playing" || session.state === "paused");
    const hasTrack = playingMedia.length > 0 || sessionTrack || knownPausedMedia;

    const duration = Number(activeMedia?.duration);
    const position = Number(activeMedia?.currentTime);
    const cover = getArtwork(session.metadata, playerRoot);
    const trackTitle = hasTrack && !titleLooksGeneric ? rawTitle : (hasTrack ? "Canción desconocida" : "Esperando una canción");
    const trackId = [
      clean(session.metadata?.title),
      clean(session.metadata?.artist),
      clean(activeMedia?.currentSrc || activeMedia?.src || ""),
      songUrl
    ].join("|");

    return {
      hasTrack,
      title: trackTitle,
      artist: hasTrack ? artist : "Suno",
      album: hasTrack ? album : "",
      coverUrl: hasTrack ? cover : null,
      trackId: hasTrack ? trackId : "",
      pageUrl: songUrl,
      durationSeconds: Number.isFinite(duration) && duration > 0 ? Math.floor(duration) : 0,
      positionSeconds: Number.isFinite(position) && position > 0 ? position : 0,
      isPlaying: hasTrack && (playingMedia.length > 0 || session.state === "playing"),
      source: "suno",
      mediaSessionAvailable: session.available,
      mediaSessionHasMetadata: session.hasMetadata,
      mediaSessionState: session.state,
      audioElementCount: audioElements.length,
      videoElementCount: videoElements.length,
      playingMediaCount: playingMedia.length,
      diagnosticMessage: `MediaSession=${session.available ? session.state : "no disponible"}; metadata=${session.hasMetadata ? "sí" : "no"}; audio=${audioElements.length}; vídeo=${videoElements.length}; reproduciendo=${playingMedia.length}`
    };
  };

  let lastPayload = "";
  let lastPostAt = 0;
  const report = () => {
    try {
      const payload = extract();
      const serialized = JSON.stringify(payload);
      const now = Date.now();
      if (serialized !== lastPayload || now - lastPostAt >= 1800) {
        lastPayload = serialized;
        lastPostAt = now;
        window.postMessage({ source: SOURCE, payload }, location.origin);
      }
    } catch (error) {
      window.postMessage({
        source: SOURCE + "_ERROR",
        message: String(error)
      }, location.origin);
    }
  };

  const reportSoon = () => window.setTimeout(report, 50);
  document.addEventListener("play", event => {
    if (event.target instanceof HTMLMediaElement) lastActiveMedia = event.target;
    reportSoon();
  }, true);
  document.addEventListener("playing", reportSoon, true);
  document.addEventListener("pause", reportSoon, true);
  document.addEventListener("ended", event => {
    if (event.target === lastActiveMedia) lastActiveMedia = null;
    reportSoon();
  }, true);
  document.addEventListener("loadedmetadata", reportSoon, true);
  document.addEventListener("durationchange", reportSoon, true);
  document.addEventListener("seeked", reportSoon, true);
  window.setInterval(report, 900);
  new MutationObserver(reportSoon).observe(document.documentElement, {
    subtree: true, childList: true, characterData: true, attributes: true,
    attributeFilter: ["src", "data-src", "aria-label", "class"]
  });
  report();
})();
