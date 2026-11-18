"use strict";

const LOCAL_TRACK_URL = "http://127.0.0.1:47831/track";

async function postToDesktop(payload) {
  const response = await fetch(LOCAL_TRACK_URL, {
    method: "POST",
    mode: "cors",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
    cache: "no-store",
    targetAddressSpace: "loopback"
  });

  if (!response.ok) {
    const body = await response.text().catch(() => "");
    throw new Error(`Puente local HTTP ${response.status}${body ? `: ${body.slice(0, 160)}` : ""}`);
  }
  return response.json().catch(() => ({ ok: true }));
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "ARIDEEV_TEST_BRIDGE") {
    const ownExtensionPrefix = chrome.runtime.getURL("");
    if (!sender.url?.startsWith(ownExtensionPrefix)) {
      sendResponse({ ok: false, error: "La prueba solo puede iniciarse desde el popup de Arideev." });
      return false;
    }

    postToDesktop({
      hasTrack: false,
      source: "extension-test",
      sourceId: "extension-connection-test",
      diagnosticMessage: "Popup y service worker conectados correctamente con la aplicación de escritorio.",
      mediaSessionState: "test"
    })
      .then(() => sendResponse({ ok: true }))
      .catch(error => {
        console.error("[Arideev Suno Presence] La prueba del service worker falló:", error);
        sendResponse({ ok: false, error: String(error?.message || error) });
      });
    return true;
  }

  if (message?.type !== "ARIDEEV_TRACK" || !message.payload) return false;

  const tabUrl = sender.tab?.url || "";
  if (!/^https:\/\/(?:[^/]+\.)?suno\.com\//i.test(tabUrl)) {
    sendResponse({ ok: false, error: "Origen no permitido: abre Suno en una pestaña normal." });
    return false;
  }

  postToDesktop({ ...message.payload, sourceId: String(sender.tab.id) })
    .then(() => sendResponse({ ok: true }))
    .catch(error => {
      console.error("[Arideev Suno Presence] Error enviando datos al escritorio:", error);
      sendResponse({ ok: false, error: String(error?.message || error) });
    });

  return true;
});
