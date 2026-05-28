const CACHE_NAME = "padaria-pdv-v3";
const ASSETS_TO_CACHE = [
  "/login",
  "/manifest.json",
  "/icon-512.png"
];

// Instalação: Cacheia os caminhos estáticos e públicos essenciais da interface
self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE_NAME).then((cache) => {
      console.log("[Service Worker] Cacheando assets fundamentais...");
      return cache.addAll(ASSETS_TO_CACHE).catch((err) => {
        console.warn("[Service Worker] Falha ao pré-cachear assets básicos:", err);
      });
    })
  );
  self.skipWaiting();
});

// Ativação: Limpa caches antigos
self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys().then((keys) => {
      return Promise.all(
        keys.map((key) => {
          if (key !== CACHE_NAME) {
            console.log("[Service Worker] Removendo cache antigo:", key);
            return caches.delete(key);
          }
        })
      );
    })
  );
  self.clients.claim();
});

// Interceptação de Requisições (Fetch)
// Estratégia: Network First com Fallback de Cache para manter atualizado mas resiliente offline
self.addEventListener("fetch", (event) => {
  // Ignora requisições de APIs internas (como criar vendas) e WebSockets, focando em assets de tela
  const url = new URL(event.request.url);
  if (
    url.pathname.startsWith("/api") || 
    event.request.method !== "GET" ||
    url.pathname.startsWith("/_next") || // evita cachear hot-reloading dev
    url.pathname.includes("webpack")
  ) {
    return;
  }

  event.respondWith(
    fetch(event.request)
      .then((networkResponse) => {
        // Se a requisição de rede deu certo e é do tipo GET 200, guarda no cache dinamicamente
        if (networkResponse.status === 200) {
          const responseClone = networkResponse.clone();
          caches.open(CACHE_NAME).then((cache) => {
            cache.put(event.request, responseClone);
          });
        }
        return networkResponse;
      })
      .catch(() => {
        // Se a rede falhar (Offline!), busca no cache
        console.log("[Service Worker] Offline detectado, buscando asset no cache local:", event.request.url);
        return caches.match(event.request).then((cachedResponse) => {
          if (cachedResponse) {
            return cachedResponse;
          }
          // Caso não ache nada, se for uma navegação de página, retorna a página login/pdv
          if (event.request.mode === "navigate") {
            return caches.match("/login") || caches.match("/pdv");
          }
        });
      })
  );
});
