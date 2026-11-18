"use strict";

const LOCAL_HEALTH_URL = "http://127.0.0.1:47831/health";
const connectButton = document.getElementById("connect");
const statusBox = document.getElementById("status");
const permissionBox = document.getElementById("permission");

function showStatus(message, kind = "") {
  statusBox.textContent = message;
  statusBox.className = kind;
}

async function readLoopbackPermission() {
  try {
    if (!navigator.permissions?.query) return "no disponible";
    const permission = await navigator.permissions.query({ name: "loopback-network" });
    return permission.state;
  } catch {
    // Some Chromium/Edge builds do not expose this PermissionName to extension pages.
    // The explicit fetch below is the authoritative capability test.
    return "no expuesto por el navegador";
  }
}

(async () => {
  const state = await readLoopbackPermission();
  permissionBox.textContent = `Permiso de red local: ${state}`;
  if (state === "granted") {
    showStatus("Permiso concedido. Pulsa «Conectar» para comprobar que SunoSync está abierto.", "ok");
  } else if (state === "denied") {
    showStatus("El navegador ha bloqueado el acceso local. Revisa los permisos de red local / loopback del navegador.", "bad");
  }
})();

connectButton.addEventListener("click", async () => {
  connectButton.disabled = true;
  showStatus("Comprobando SunoSync y solicitando acceso local si el navegador lo necesita…");
  try {
    // This request is deliberately made from the popup's document and directly
    // inside the click handler. MV3 service workers cannot initiate the first
    // LNA permission prompt because they have no active Document/user gesture.
    const response = await fetch(LOCAL_HEALTH_URL, {
      method: "GET",
      mode: "cors",
      cache: "no-store",
      targetAddressSpace: "loopback"
    });
    if (!response.ok) throw new Error(`Arideev respondió HTTP ${response.status}.`);
    const health = await response.json();
    if (!health?.ok) throw new Error("La aplicación respondió, pero no confirmó el estado del puente.");

    // Verify the MV3 service-worker path as well. This is the exact path used
    // for every Suno playback heartbeat and track update.
    const workerTest = await chrome.runtime.sendMessage({ type: "ARIDEEV_TEST_BRIDGE" });
    if (!workerTest?.ok)
      throw new Error(`El popup llega a Arideev, pero el service worker no puede enviar datos: ${workerTest?.error || "sin respuesta"}`);

    const permissionState = await readLoopbackPermission();
    permissionBox.textContent = `Permiso de red local: ${permissionState}`;
    showStatus("Conexión local y service worker verificados. Vuelve a Suno, recarga la pestaña (Ctrl+R) y reproduce una canción.", "ok");
  } catch (error) {
    const message = String(error?.message || error);
    showStatus(
      `No se pudo conectar con SunoSync. Comprueba que la aplicación de Windows siga abierta. Si el navegador pregunta por acceso a la red local o loopback, pulsa «Permitir». Detalle: ${message}`,
      "bad"
    );
    const permissionState = await readLoopbackPermission();
    permissionBox.textContent = `Permiso de red local: ${permissionState}`;
  } finally {
    connectButton.disabled = false;
  }
});
