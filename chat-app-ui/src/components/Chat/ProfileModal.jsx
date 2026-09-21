import { useState, useRef, useEffect } from 'react';
import { userAPI, uploadAPI, API_BASE, getFullAvatarUrl } from '../../services/api';
import { useAuth } from '../../context/AuthContext';

export default function ProfileModal({ isOpen, onClose }) {
  const { user, updateUser } = useAuth();
  const [displayName, setDisplayName] = useState('');
  const [profilePictureUrl, setProfilePictureUrl] = useState('');
  const [previewUrl, setPreviewUrl] = useState(null);
  const [uploading, setUploading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const fileInputRef = useRef(null);

  useEffect(() => {
    if (isOpen && user) {
      setDisplayName(user.displayName || '');
      setProfilePictureUrl(user.profilePictureUrl || '');
      setPreviewUrl(null);
      setError('');
      setSuccess('');
    }
  }, [isOpen, user]);

  if (!isOpen || !user) return null;

  const handleFileChange = async (e) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (!file.type.startsWith('image/')) {
      setError('Please select a valid image file (JPG, PNG, WEBP)');
      return;
    }

    if (file.size > 10 * 1024 * 1024) {
      setError('Image size exceeds maximum limit of 10MB');
      return;
    }

    // Local preview immediately
    const reader = new FileReader();
    reader.onload = (event) => {
      setPreviewUrl(event.target?.result);
    };
    reader.readAsDataURL(file);

    // Upload to server
    setUploading(true);
    setError('');
    try {
      const res = await uploadAPI.uploadFile(file);
      setProfilePictureUrl(res.data.url);
      setSuccess('Image uploaded! Click "Save Changes" to apply.');
    } catch (err) {
      console.error('Failed to upload image:', err);
      setError(err.response?.data?.error || 'Failed to upload photo');
      setPreviewUrl(null);
    } finally {
      setUploading(false);
    }
  };

  const handleRemovePhoto = () => {
    setProfilePictureUrl('');
    setPreviewUrl(null);
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  };

  const handleSave = async (e) => {
    e.preventDefault();
    if (!displayName.trim()) {
      setError('Display name cannot be empty');
      return;
    }

    setSaving(true);
    setError('');
    setSuccess('');

    try {
      const response = await userAPI.updateProfile({
        displayName: displayName.trim(),
        profilePictureUrl: profilePictureUrl || null,
      });

      const updated = response.data;
      updateUser(updated);
      setSuccess('Profile updated successfully!');
      setTimeout(() => {
        onClose();
      }, 700);
    } catch (err) {
      console.error('Error updating profile:', err);
      setError(err.response?.data?.error || 'Failed to save changes');
    } finally {
      setSaving(false);
    }
  };

  const currentAvatar = previewUrl || getFullAvatarUrl(profilePictureUrl);
  const initials = (displayName || user.username || 'U')
    .split(' ')
    .map((n) => n[0])
    .join('')
    .toUpperCase()
    .substring(0, 2);

  return (
    <div className="modal-overlay-glass" onClick={onClose}>
      <div
        className="modal-card-premium profile-modal-card"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="modal-header-premium">
          <div className="modal-header-left">
            <div className="modal-icon-badge">
              <span>👤</span>
            </div>
            <div>
              <h3 className="modal-title-premium">Edit Profile</h3>
              <p className="modal-subtitle-premium">Customize your name and profile picture</p>
            </div>
          </div>
          <button
            type="button"
            className="modal-close-btn-premium"
            onClick={onClose}
            aria-label="Close"
          >
            ✕
          </button>
        </div>

        <form onSubmit={handleSave} className="modal-form-wrapper">
          <div className="modal-body-scrollable">
            {error && (
              <div className="modal-alert-error">
                <span className="alert-icon">⚠️</span>
                <span>{error}</span>
              </div>
            )}

            {success && (
              <div className="modal-alert-success">
                <span className="alert-icon">✅</span>
                <span>{success}</span>
              </div>
            )}

            {/* Profile Avatar Upload Centerpiece */}
            <div className="profile-avatar-centerpiece">
              <input
                type="file"
                ref={fileInputRef}
                onChange={handleFileChange}
                accept="image/*"
                style={{ display: 'none' }}
              />

              <div
                className="profile-avatar-clickable"
                onClick={() => fileInputRef.current?.click()}
                title="Click to upload profile photo"
              >
                {currentAvatar ? (
                  <img
                    src={currentAvatar}
                    alt="Profile Avatar"
                    className="profile-avatar-large-img"
                    onError={(e) => {
                      // Fallback gracefully if image URL fails to load
                      e.currentTarget.style.display = 'none';
                      const fallback = e.currentTarget.nextElementSibling;
                      if (fallback) fallback.style.display = 'flex';
                    }}
                  />
                ) : null}

                <div
                  className="profile-avatar-large-placeholder"
                  style={{ display: currentAvatar ? 'none' : 'flex' }}
                >
                  {initials}
                </div>

                {/* Camera Overlay */}
                <div className="avatar-camera-overlay">
                  {uploading ? (
                    <div className="spinner-glow" style={{ width: '28px', height: '28px' }} />
                  ) : (
                    <>
                      <span className="camera-icon">📷</span>
                      <span className="camera-text">Change Photo</span>
                    </>
                  )}
                </div>
              </div>

              {profilePictureUrl && (
                <button
                  type="button"
                  className="btn-remove-photo"
                  onClick={handleRemovePhoto}
                  disabled={uploading || saving}
                >
                  🗑️ Remove Photo
                </button>
              )}
            </div>

            {/* Display Name Field */}
            <div className="form-group-premium">
              <label className="form-label-premium">Display Name</label>
              <div className="input-with-icon-wrapper">
                <span className="input-prefix-icon">✏️</span>
                <input
                  type="text"
                  className="input-premium"
                  placeholder="Your full name or nickname"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  maxLength={50}
                  required
                />
              </div>
            </div>

            {/* Read-only User Details */}
            <div className="form-group-premium">
              <label className="form-label-premium">Username</label>
              <div className="input-with-icon-wrapper">
                <span className="input-prefix-icon">@</span>
                <input
                  type="text"
                  className="input-premium read-only"
                  value={user.username || ''}
                  readOnly
                  disabled
                />
              </div>
            </div>

            <div className="form-group-premium">
              <label className="form-label-premium">Email Address</label>
              <div className="input-with-icon-wrapper">
                <span className="input-prefix-icon">📧</span>
                <input
                  type="email"
                  className="input-premium read-only"
                  value={user.email || ''}
                  readOnly
                  disabled
                />
              </div>
            </div>
          </div>

          {/* Footer */}
          <div className="modal-footer-premium">
            <button
              type="button"
              className="btn-modal-secondary"
              onClick={onClose}
              disabled={saving || uploading}
            >
              Cancel
            </button>
            <button
              type="submit"
              className="btn-modal-primary"
              disabled={saving || uploading || !displayName.trim()}
            >
              {saving ? (
                <>
                  <div className="btn-spinner" />
                  <span>Saving...</span>
                </>
              ) : (
                'Save Changes'
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
