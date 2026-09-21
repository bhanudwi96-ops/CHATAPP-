import axios from 'axios';

// API base URL
export const API_BASE = 'https://localhost:7172';
export const API_URL = `${API_BASE}/api`;

// Helper: Normalize avatar URL and bypass slow/unreachable external APIs
export const getFullAvatarUrl = (url) => {
  if (!url) return null;
  if (url.includes('api.dicebear.com')) return null;
  if (url.startsWith('http://') || url.startsWith('https://')) return url;
  return `${API_BASE}${url}`;
};

// Create axios instance with default config
const api = axios.create({
  baseURL: API_URL,
  headers: {
    'Content-Type': 'application/json',
  },
});

// Add token to requests from sessionStorage (or legacy localStorage)
api.interceptors.request.use(
  (config) => {
    const token = sessionStorage.getItem('token') || localStorage.getItem('token');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => {
    return Promise.reject(error);
  }
);

// Handle 401 Unauthorized (expired token / session)
api.interceptors.response.use(
  (response) => response,
  (error) => {
    if (error.response && error.response.status === 401) {
      console.warn('⚠️ Session expired (401 Unauthorized). Clearing credentials.');
      sessionStorage.removeItem('token');
      sessionStorage.removeItem('user');
      localStorage.removeItem('token');
      localStorage.removeItem('user');
      if (window.location.pathname !== '/login' && window.location.pathname !== '/register') {
        window.location.href = '/login';
      }
    }
    return Promise.reject(error);
  }
);

// Auth endpoints
export const authAPI = {
  register: (data) => api.post('/Auth/register', data),
  login: (data) => api.post('/Auth/login', data),
  forgotPassword: (email) => api.post('/Auth/forgot-password', { email }),
  verifyOtp: (email, otp) => api.post('/Auth/forgot-password/verify-otp', { email, otp }),
  resetPassword: (data) => api.post('/Auth/reset-password', data),
};

// User search, directory & profile endpoints
export const userAPI = {
  getMe: () => api.get('/User/me'),
  updateProfile: (data) => api.put('/User/profile', data),
  updateProfilePicture: (profilePictureUrl) =>
    api.put('/User/profile-picture', { profilePictureUrl }),
  search: (query = '') => api.get(`/User/search?query=${encodeURIComponent(query)}`),
  getAll: () => api.get('/User/all'),
};

// File upload endpoints
export const uploadAPI = {
  uploadFile: (file) => {
    const formData = new FormData();
    formData.append('file', file);
    return api.post('/Upload', formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    });
  },
};

// Conversation endpoints
export const conversationAPI = {
  getAll: () => api.get('/Conversation'),
  getById: (id) => api.get(`/Conversation/${id}`),
  create: (data) => api.post('/Conversation', data),
  createPrivate: (userId) => api.post(`/Conversation/private/${userId}`),
  delete: (id) => api.delete(`/Conversation/${id}`),
  addParticipants: (id, userIds) => api.post(`/Conversation/${id}/participants`, { userIds }),
  removeParticipant: (id, userId) => api.delete(`/Conversation/${id}/participants/${userId}`),
  leave: (id) => api.post(`/Conversation/${id}/leave`),
  setAdmin: (id, userId, isAdmin) => api.put(`/Conversation/${id}/participants/${userId}/admin`, { isAdmin }),
};

// Message endpoints
export const messageAPI = {
  getConversationMessages: (conversationId, skip = 0, take = 50) =>
    api.get(`/Message/conversation/${conversationId}?skip=${skip}&take=${take}`),
  getById: (id) => api.get(`/Message/${id}`),
  send: (data) => api.post('/Message', data),
  edit: (id, content) => api.put(`/Message/${id}`, { messageId: id, content }),
  markAsRead: (id) => api.put(`/Message/${id}/read`),
  markConversationAsRead: (conversationId) =>
    api.put(`/Message/conversation/${conversationId}/read`),
  delete: (id) => api.delete(`/Message/${id}`),
};

export default api;
