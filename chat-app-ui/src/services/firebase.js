import { initializeApp } from 'firebase/app';
import { getMessaging, getToken, onMessage } from 'firebase/messaging';

// ────────────────────────────────────────────────────────────────
// Firebase web app config — fill these from Firebase Console
// Project Settings → General → Your apps → Web app → Config
// ────────────────────────────────────────────────────────────────
const firebaseConfig = {
  apiKey:            import.meta.env.VITE_FIREBASE_API_KEY,
  authDomain:        import.meta.env.VITE_FIREBASE_AUTH_DOMAIN,
  projectId:         import.meta.env.VITE_FIREBASE_PROJECT_ID,
  storageBucket:     import.meta.env.VITE_FIREBASE_STORAGE_BUCKET,
  messagingSenderId: import.meta.env.VITE_FIREBASE_MESSAGING_SENDER_ID,
  appId:             import.meta.env.VITE_FIREBASE_APP_ID,
};

// VAPID key from Firebase Console → Cloud Messaging → Web Push Certificates
const VAPID_KEY = import.meta.env.VITE_FIREBASE_VAPID_KEY;

let app = null;
let messaging = null;

function getFirebaseApp() {
  if (!app) {
    app = initializeApp(firebaseConfig);
  }
  return app;
}

function getFirebaseMessaging() {
  if (!messaging) {
    messaging = getMessaging(getFirebaseApp());
  }
  return messaging;
}

/**
 * Request notification permission and return the FCM device token.
 * Returns null if permission denied or Firebase config is missing.
 */
export async function requestFcmToken() {
  if (!firebaseConfig.apiKey) {
    console.warn('[FCM] Firebase config missing — push notifications disabled');
    return null;
  }

  try {
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') {
      console.log('[FCM] Notification permission denied');
      return null;
    }

    // Register SW first, then wait for it to become active
    await navigator.serviceWorker.register('/service-worker.js');
    const swRegistration = await navigator.serviceWorker.ready;

    const token = await getToken(getFirebaseMessaging(), {
      vapidKey: VAPID_KEY,
      serviceWorkerRegistration: swRegistration,
    });

    console.log('[FCM] Device token obtained:', token?.slice(0, 20) + '...');
    return token;
  } catch (err) {
    console.error('[FCM] Failed to get device token:', err);
    return null;
  }
}

/**
 * Listen for foreground messages (app is open/focused).
 * @param {(payload: object) => void} handler
 */
export function onForegroundMessage(handler) {
  if (!firebaseConfig.apiKey) return () => {};
  return onMessage(getFirebaseMessaging(), handler);
}
