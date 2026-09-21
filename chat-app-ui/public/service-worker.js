// Firebase Messaging Service Worker
// Handles push events when the app is in the background or closed.
// Must be in /public so Vite serves it at the root: /service-worker.js

importScripts('https://www.gstatic.com/firebasejs/10.12.0/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/10.12.0/firebase-messaging-compat.js');

// ── Firebase config (duplicated here — service workers can't import env vars) ──
// Replace these values with the ones from your Firebase Console web app config.
firebase.initializeApp({
  apiKey:            'AIzaSyD1z2WnUAh2wCPa5624IaUlLBT6Im4KG_k',
  authDomain:        'chat-app-cdfa2.firebaseapp.com',
  projectId:         'chat-app-cdfa2',
  storageBucket:     'chat-app-cdfa2.firebasestorage.app',
  messagingSenderId: '909110270155',
  appId:             '1:909110270155:web:dbf395bea017c64845a34e',
});

const messaging = firebase.messaging();

// ── Background message handler ──────────────────────────────────────────────
messaging.onBackgroundMessage((payload) => {
  console.log('[SW] Background push received:', payload);

  const notificationTitle = payload.notification?.title || 'New message';
  const notificationBody  = payload.notification?.body  || '';
  const conversationId    = payload.data?.conversationId || '';

  self.registration.showNotification(notificationTitle, {
    body:  notificationBody,
    icon:  '/vite.svg',
    badge: '/vite.svg',
    tag:   conversationId,          // deduplicates per-conversation
    renotify: true,
    data:  { conversationId },
  });
});

// ── Notification click: open/focus the app and go to that conversation ───────
self.addEventListener('notificationclick', (event) => {
  event.notification.close();

  const conversationId = event.notification.data?.conversationId;
  const url = conversationId
    ? `${self.location.origin}/chat/${conversationId}`
    : self.location.origin;

  event.waitUntil(
    clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientList) => {
      // If the app is already open, focus it and navigate
      for (const client of clientList) {
        if (client.url.startsWith(self.location.origin) && 'focus' in client) {
          client.focus();
          if (conversationId) {
            client.postMessage({ type: 'OPEN_CONVERSATION', conversationId });
          }
          return;
        }
      }
      // Otherwise open a new window
      if (clients.openWindow) {
        return clients.openWindow(url);
      }
    })
  );
});
