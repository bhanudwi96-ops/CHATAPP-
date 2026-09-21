import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { authAPI } from '../../services/api';
import './Auth.css';

export default function ForgotPassword() {
  const navigate = useNavigate();

  // Wizard step: 1 = Enter Email, 2 = Enter OTP, 3 = Reset Password, 4 = Success
  const [step, setStep] = useState(1);

  // Form states
  const [email, setEmail] = useState('');
  const [otp, setOtp] = useState('');
  const [resetToken, setResetToken] = useState('');
  const [oldPassword, setOldPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showOldPassword, setShowOldPassword] = useState(false);
  const [showNewPassword, setShowNewPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);

  // UI state
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [successInfo, setSuccessInfo] = useState('');

  // Step 1: Request 6-digit OTP
  const handleRequestOtp = async (e) => {
    e.preventDefault();
    if (!email.trim()) {
      setError('Please enter your email address');
      return;
    }

    setError('');
    setLoading(true);

    try {
      const res = await authAPI.forgotPassword(email.trim());
      setSuccessInfo(res.data?.message || 'If an account exists, a 6-digit verification code has been sent.');
      setStep(2);
    } catch (err) {
      setError(err.response?.data?.error || 'Failed to send verification code. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  // Step 2: Verify OTP code
  const handleVerifyOtp = async (e) => {
    e.preventDefault();
    if (!otp.trim() || otp.trim().length !== 6) {
      setError('Please enter a valid 6-digit verification code');
      return;
    }

    setError('');
    setLoading(true);

    try {
      const res = await authAPI.verifyOtp(email.trim(), otp.trim());
      if (res.data?.verified && res.data?.resetToken) {
        setResetToken(res.data.resetToken);
        setSuccessInfo('Code verified successfully. Now verify your old password and set your new password.');
        setStep(3);
      } else {
        setError(res.data?.message || 'Verification failed. Please check the code.');
      }
    } catch (err) {
      setError(err.response?.data?.error || err.response?.data?.message || 'Invalid or expired verification code.');
    } finally {
      setLoading(false);
    }
  };

  // Resend OTP in Step 2
  const handleResendOtp = async () => {
    setError('');
    setLoading(true);
    try {
      await authAPI.forgotPassword(email.trim());
      setSuccessInfo('A fresh 6-digit code has been dispatched to your email.');
      setOtp('');
    } catch (err) {
      setError(err.response?.data?.error || 'Failed to resend code.');
    } finally {
      setLoading(false);
    }
  };

  // Step 3: Complete Password Reset
  const handleResetPassword = async (e) => {
    e.preventDefault();
    setError('');

    if (!oldPassword) {
      setError('Please enter your current (old) password');
      return;
    }

    if (newPassword.length < 6) {
      setError('New password must be at least 6 characters');
      return;
    }

    if (newPassword === oldPassword) {
      setError('New password cannot be the same as your old password');
      return;
    }

    if (newPassword !== confirmPassword) {
      setError('New password and confirmation do not match');
      return;
    }

    setLoading(true);

    try {
      await authAPI.resetPassword({
        email: email.trim(),
        resetToken,
        oldPassword,
        newPassword
      });

      setStep(4);
    } catch (err) {
      setError(err.response?.data?.error || 'Failed to reset password. Please check your inputs.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="auth-container">
      <div className="auth-box" style={{ maxWidth: '460px' }}>
        {/* Step Indicator */}
        <div style={{ display: 'flex', justifyContent: 'center', gap: '8px', marginBottom: '20px' }}>
          {[1, 2, 3].map((s) => (
            <div
              key={s}
              style={{
                width: '32px',
                height: '4px',
                borderRadius: '2px',
                background: step >= s ? 'linear-gradient(135deg, #00f2fe, #0ea5e9)' : 'rgba(255, 255, 255, 0.15)',
                transition: 'background 0.3s ease'
              }}
            />
          ))}
        </div>

        {/* STEP 1: Enter Email */}
        {step === 1 && (
          <>
            <h1 className="auth-title">Forgot Password?</h1>
            <p className="auth-subtitle">
              Enter your registered email and we'll send you a 6-digit verification code
            </p>

            {error && <div className="error-message">{error}</div>}

            <form onSubmit={handleRequestOtp} className="auth-form">
              <div className="form-group">
                <label htmlFor="email">Registered Email</label>
                <div className="input-wrapper">
                  <span className="input-icon-left">✉️</span>
                  <input
                    id="email"
                    type="email"
                    className="has-icon-left"
                    required
                    placeholder="name@example.com"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    disabled={loading}
                  />
                </div>
              </div>

              <button type="submit" className="btn-primary" disabled={loading || !email.trim()}>
                {loading ? 'Sending Code...' : 'Send Verification Code'}
              </button>
            </form>

            <p className="auth-footer" style={{ marginTop: '24px' }}>
              Remember your password? <Link to="/login">Back to Login</Link>
            </p>
          </>
        )}

        {/* STEP 2: Enter OTP */}
        {step === 2 && (
          <>
            <h1 className="auth-title">Verify Code</h1>
            <p className="auth-subtitle">
              Enter the 6-digit code sent to <br />
              <strong style={{ color: '#38bdf8' }}>{email}</strong>
            </p>

            {successInfo && <div className="success-message" style={{ marginBottom: '16px' }}>{successInfo}</div>}
            {error && <div className="error-message">{error}</div>}

            <form onSubmit={handleVerifyOtp} className="auth-form">
              <div className="form-group">
                <label htmlFor="otp">6-Digit Verification Code</label>
                <input
                  id="otp"
                  type="text"
                  maxLength={6}
                  required
                  autoFocus
                  placeholder="e.g. 483921"
                  value={otp}
                  onChange={(e) => setOtp(e.target.value.replace(/\D/g, ''))}
                  disabled={loading}
                  style={{
                    letterSpacing: '6px',
                    fontSize: '22px',
                    textAlign: 'center',
                    fontWeight: '700',
                    color: '#0f172a'
                  }}
                />
              </div>

              <button type="submit" className="btn-primary" disabled={loading || otp.length !== 6}>
                {loading ? 'Verifying...' : 'Verify Code'}
              </button>

              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '10px' }}>
                <button
                  type="button"
                  onClick={handleResendOtp}
                  disabled={loading}
                  style={{
                    background: 'none',
                    border: 'none',
                    color: '#38bdf8',
                    fontSize: '13px',
                    cursor: 'pointer',
                    textDecoration: 'underline'
                  }}
                >
                  Resend Code
                </button>

                <button
                  type="button"
                  onClick={() => { setStep(1); setError(''); }}
                  disabled={loading}
                  style={{
                    background: 'none',
                    border: 'none',
                    color: '#64748b',
                    fontSize: '13px',
                    cursor: 'pointer'
                  }}
                >
                  Change Email
                </button>
              </div>
            </form>
          </>
        )}

        {/* STEP 3: Reset Password */}
        {step === 3 && (
          <>
            <h1 className="auth-title">Set New Password</h1>
            <p className="auth-subtitle">
              Verify your old password and set a secure new password
            </p>

            {error && <div className="error-message">{error}</div>}

            <form onSubmit={handleResetPassword} className="auth-form">
              <div className="form-group">
                <label htmlFor="oldPassword">Current (Old) Password</label>
                <div className="input-wrapper">
                  <span className="input-icon-left">🔒</span>
                  <input
                    id="oldPassword"
                    type={showOldPassword ? 'text' : 'password'}
                    className="has-icon-left has-toggle-right"
                    required
                    placeholder="Enter your current password"
                    value={oldPassword}
                    onChange={(e) => setOldPassword(e.target.value)}
                    disabled={loading}
                  />
                  <button
                    type="button"
                    className="input-toggle-btn"
                    onClick={() => setShowOldPassword(!showOldPassword)}
                    aria-label={showOldPassword ? 'Hide password' : 'Show password'}
                    tabIndex={-1}
                  >
                    {showOldPassword ? '🙈' : '👁️'}
                  </button>
                </div>
              </div>

              <div className="form-group">
                <label htmlFor="newPassword">New Password (min 6 characters)</label>
                <div className="input-wrapper">
                  <span className="input-icon-left">✨</span>
                  <input
                    id="newPassword"
                    type={showNewPassword ? 'text' : 'password'}
                    className="has-icon-left has-toggle-right"
                    required
                    minLength={6}
                    placeholder="Create a new password"
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                    disabled={loading}
                  />
                  <button
                    type="button"
                    className="input-toggle-btn"
                    onClick={() => setShowNewPassword(!showNewPassword)}
                    aria-label={showNewPassword ? 'Hide password' : 'Show password'}
                    tabIndex={-1}
                  >
                    {showNewPassword ? '🙈' : '👁️'}
                  </button>
                </div>
              </div>

              <div className="form-group">
                <label htmlFor="confirmPassword">Confirm New Password</label>
                <div className="input-wrapper">
                  <span className="input-icon-left">🛡️</span>
                  <input
                    id="confirmPassword"
                    type={showConfirmPassword ? 'text' : 'password'}
                    className="has-icon-left has-toggle-right"
                    required
                    minLength={6}
                    placeholder="Re-enter new password"
                    value={confirmPassword}
                    onChange={(e) => setConfirmPassword(e.target.value)}
                    disabled={loading}
                  />
                  <button
                    type="button"
                    className="input-toggle-btn"
                    onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                    aria-label={showConfirmPassword ? 'Hide password' : 'Show password'}
                    tabIndex={-1}
                  >
                    {showConfirmPassword ? '🙈' : '👁️'}
                  </button>
                </div>
              </div>

              <button
                type="submit"
                className="btn-primary"
                disabled={loading || !oldPassword || newPassword.length < 6 || newPassword !== confirmPassword}
              >
                {loading ? 'Updating Password...' : 'Reset Password'}
              </button>
            </form>
          </>
        )}

        {/* STEP 4: Success State */}
        {step === 4 && (
          <div style={{ textAlign: 'center', padding: '10px 0' }}>
            <div
              style={{
                width: '64px',
                height: '64px',
                borderRadius: '50%',
                background: '#ecfdf5',
                color: '#10b981',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                fontSize: '32px',
                fontWeight: '700',
                border: '2px solid #a7f3d0',
                margin: '0 auto 20px auto'
              }}
            >
              ✓
            </div>

            <h1 className="auth-title">Password Changed!</h1>
            <p className="auth-subtitle" style={{ marginBottom: '24px' }}>
              Your password has been updated securely. You can now log in using your new credentials.
            </p>

            <button
              type="button"
              className="btn-primary"
              onClick={() => navigate('/login')}
            >
              Proceed to Login
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
