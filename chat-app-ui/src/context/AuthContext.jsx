import { createContext, useContext, useState, useEffect } from 'react';
import { authAPI } from '../services/api';
import signalRService from '../services/signalr';
import { requestFcmToken, onForegroundMessage } from '../services/firebase';

const AuthContext = createContext(null);

export const useAuth = () => {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider');
  }
  return context;
};

export const AuthProvider = ({ children }) => {
  const [user, setUser] = useState(null);
  const [token, setToken] = useState(null);
  const [loading, setLoading] = useState(true);

  // Helper to check if a JWT token is expired
  const isTokenExpired = (jwtToken) => {
    try {
      const payloadBase64 = jwtToken.split('.')[1];
      if (!payloadBase64) return true;
      const payloadJson = atob(payloadBase64.replace(/-/g, '+').replace(/_/g, '/'));
      const payload = JSON.parse(payloadJson);
      if (!payload.exp) return false;
      return payload.exp * 1000 < Date.now();
    } catch (e) {
      console.warn('Invalid JWT format, treating as expired:', e);
      return true;
    }
  };

  // Load user from sessionStorage on mount
  useEffect(() => {
    localStorage.removeItem('token');
    localStorage.removeItem('user');

    const storedToken = sessionStorage.getItem('token');
    const storedUser = sessionStorage.getItem('user');

    if (storedToken && storedUser && !isTokenExpired(storedToken)) {
      setToken(storedToken);
      try {
        setUser(JSON.parse(storedUser));
      } catch {
        setUser(null);
      }
      
      signalRService.connect(storedToken)
        .then(() => {
          console.log('✅ SignalR connected successfully');
        })
        .catch((error) => {
          console.error('❌ Failed to connect to SignalR:', error);
        });
    } else {
      sessionStorage.removeItem('token');
      sessionStorage.removeItem('user');
      setToken(null);
      setUser(null);
    }

    setLoading(false);

    const handleBeforeUnload = async () => {
      if (signalRService.isConnected) {
        await signalRService.disconnect();
      }
    };

    window.addEventListener('beforeunload', handleBeforeUnload);

    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
    };
  }, []);

  // ── Handle service-worker notification click → navigate to conversation ──
  useEffect(() => {
    const handleSwMessage = (event) => {
      if (event.data?.type === 'OPEN_CONVERSATION' && event.data.conversationId) {
        window.location.href = `/chat/${event.data.conversationId}`;
      }
    };
    navigator.serviceWorker?.addEventListener('message', handleSwMessage);
    return () => navigator.serviceWorker?.removeEventListener('message', handleSwMessage);
  }, []);

  // ── Register FCM token with our API ─────────────────────────────────────
  const registerPushToken = async (authToken) => {
    try {
      const fcmToken = await requestFcmToken();
      if (!fcmToken) return;
      await fetch('/api/notifications/device', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${authToken}`,
        },
        body: JSON.stringify({ token: fcmToken }),
      });
      sessionStorage.setItem('fcmToken', fcmToken);
      console.log('[FCM] Device token registered with server');
    } catch (err) {
      console.warn('[FCM] Failed to register push token:', err);
    }
  };

  const unregisterPushToken = async (authToken) => {
    const fcmToken = sessionStorage.getItem('fcmToken');
    if (!fcmToken || !authToken) return;
    try {
      await fetch('/api/notifications/device', {
        method: 'DELETE',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${authToken}`,
        },
        body: JSON.stringify({ token: fcmToken }),
      });
      sessionStorage.removeItem('fcmToken');
    } catch (err) {
      console.warn('[FCM] Failed to unregister push token:', err);
    }
  };;

  const login = async (username, password) => {
    try {
      console.time('[PERF-UI] Login API call');
      const response = await authAPI.login({ username, password });
      console.timeEnd('[PERF-UI] Login API call');
      const { token: newToken, user: newUser } = response.data;

      setToken(newToken);
      setUser(newUser);

      sessionStorage.setItem('token', newToken);
      sessionStorage.setItem('user', JSON.stringify(newUser));

      // Connect SignalR in background — DO NOT await
      // Awaiting blocks the HTTP/2 connection pool and delays conversation loading
      console.time('[PERF-UI] SignalR connect');
      signalRService.connect(newToken).then(() => {
        console.timeEnd('[PERF-UI] SignalR connect');
      }).catch((err) => {
        console.timeEnd('[PERF-UI] SignalR connect');
        console.error('SignalR connect error:', err);
      });

      // Register FCM push token in background
      registerPushToken(newToken);

      return { success: true };
    } catch (error) {
      console.timeEnd('[PERF-UI] Login API call');
      console.error('Login error:', error);
      return {
        success: false,
        error: error.response?.data?.error || 'Login failed',
      };
    }
  };

  const register = async (username, email, password, displayName) => {
    try {
      const response = await authAPI.register({
        username,
        email,
        password,
        displayName,
      });
      const { token: newToken, user: newUser } = response.data;

      setToken(newToken);
      setUser(newUser);

      sessionStorage.setItem('token', newToken);
      sessionStorage.setItem('user', JSON.stringify(newUser));

      await signalRService.connect(newToken);

      // Register FCM push token in background
      registerPushToken(newToken);

      return { success: true };
    } catch (error) {
      console.error('Registration error:', error);
      return {
        success: false,
        error: error.response?.data?.error || 'Registration failed',
      };
    }
  };

  const updateUser = (updatedUser) => {
    setUser((prev) => {
      const merged = { ...prev, ...updatedUser };
      sessionStorage.setItem('user', JSON.stringify(merged));
      return merged;
    });
  };

  const logout = async () => {
    console.log('🚪 Logging out...');
    
    // Unregister FCM token before clearing session
    await unregisterPushToken(token);

    if (signalRService.isConnected) {
      await signalRService.disconnect();
    }

    setUser(null);
    setToken(null);

    sessionStorage.removeItem('token');
    sessionStorage.removeItem('user');
    sessionStorage.removeItem('support_ai_session_id');
    sessionStorage.removeItem('support_ai_guest_name');
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    localStorage.removeItem('support_ai_session_id');
    localStorage.removeItem('support_ai_guest_name');
  };

  const value = {
    user,
    token,
    loading,
    login,
    register,
    updateUser,
    logout,
    isAuthenticated: !!token,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
