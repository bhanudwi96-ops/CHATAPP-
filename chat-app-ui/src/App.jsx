import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider, useAuth } from './context/AuthContext';
import ErrorBoundary from './components/ErrorBoundary';
import Login from './components/Auth/Login';
import Register from './components/Auth/Register';
import ForgotPassword from './components/Auth/ForgotPassword';
import Chat from './components/Chat/Chat';
import BrandSplash from './components/Common/BrandSplash';
import CustomerSupportWidget from './components/Support/CustomerSupportWidget';

// Protected route component
function ProtectedRoute({ children }) {
  const { isAuthenticated, loading } = useAuth();

  if (loading) {
    return (
      <div style={{ 
        display: 'flex', 
        alignItems: 'center', 
        justifyContent: 'center', 
        height: '100vh',
        background: '#070b14',
        fontSize: '16px',
        fontWeight: '600',
        color: '#38bdf8',
        letterSpacing: '0.5px'
      }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px' }}>
          <div style={{
            width: '32px',
            height: '32px',
            border: '3px solid rgba(14, 165, 233, 0.2)',
            borderTopColor: '#0ea5e9',
            borderRadius: '50%',
            animation: 'spin 0.8s linear infinite'
          }} />
          <span>Authenticating...</span>
        </div>
      </div>
    );
  }

  return isAuthenticated ? children : <Navigate to="/login" />;
}

// Public route component (redirect to chat if already logged in)
function PublicRoute({ children }) {
  const { isAuthenticated, loading } = useAuth();

  if (loading) {
    return (
      <div style={{ 
        display: 'flex', 
        alignItems: 'center', 
        justifyContent: 'center', 
        height: '100vh',
        background: '#070b14',
        fontSize: '16px',
        fontWeight: '600',
        color: '#38bdf8',
        letterSpacing: '0.5px'
      }}>
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px' }}>
          <div style={{
            width: '32px',
            height: '32px',
            border: '3px solid rgba(14, 165, 233, 0.2)',
            borderTopColor: '#0ea5e9',
            borderRadius: '50%',
            animation: 'spin 0.8s linear infinite'
          }} />
          <span>Loading...</span>
        </div>
      </div>
    );
  }

  return isAuthenticated ? <Navigate to="/chat" /> : children;
}

function App() {
  return (
    <BrowserRouter>
      <BrandSplash />
      <ErrorBoundary>
      <AuthProvider>
        <CustomerSupportWidget />
        <Routes>
          {/* Redirect root to login */}
          <Route path="/" element={<Navigate to="/login" />} />

          {/* Public routes */}
          <Route
            path="/login"
            element={
              <PublicRoute>
                <Login />
              </PublicRoute>
            }
          />
          <Route
            path="/register"
            element={
              <PublicRoute>
                <Register />
              </PublicRoute>
            }
          />
          <Route
            path="/forgot-password"
            element={
              <PublicRoute>
                <ForgotPassword />
              </PublicRoute>
            }
          />

          {/* Protected routes */}
          <Route
            path="/chat"
            element={
              <ProtectedRoute>
                <ErrorBoundary>
                  <Chat />
                </ErrorBoundary>
              </ProtectedRoute>
            }
          />

          {/* Catch all - redirect to login */}
          <Route path="*" element={<Navigate to="/login" />} />
        </Routes>
      </AuthProvider>
      </ErrorBoundary>
    </BrowserRouter>
  );
}

export default App;
