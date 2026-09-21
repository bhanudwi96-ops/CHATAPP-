import { useState, useEffect } from 'react';
import { getFullAvatarUrl } from '../../services/api';

export default function UserProfileModal({ isOpen, onClose, user, onStartChat }) {
  const [isZoomed, setIsZoomed] = useState(false);
  const [imageError, setImageError] = useState(false);

  useEffect(() => {
    if (isOpen) {
      setIsZoomed(false);
      setImageError(false);
    }
  }, [isOpen, user]);

  if (!isOpen || !user) return null;

  const avatarUrl = !imageError ? getFullAvatarUrl(user.profilePictureUrl) : null;
  const initials = (user.displayName || user.username || 'U')
    .split(' ')
    .map((n) => n[0])
    .join('')
    .toUpperCase()
    .substring(0, 2);

  const isOnline = user.status === 1;

  const getLastSeenText = () => {
    if (isOnline) return 'Active Now';
    if (!user.lastSeen) return 'Offline';

    const lastSeen = new Date(user.lastSeen);
    const now = new Date();
    const diffMs = now - lastSeen;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'Active just now';
    if (diffMins < 60) return `Active ${diffMins}m ago`;
    if (diffMins < 1440) return `Active ${Math.floor(diffMins / 60)}h ago`;
    return `Last seen ${lastSeen.toLocaleDateString()}`;
  };

  const handleSendMessage = () => {
    if (onStartChat) {
      onStartChat(user);
    }
    onClose();
  };

  return (
    <>
      {/* Main Profile Details Modal */}
      {!isZoomed && (
        <div className="modal-overlay-glass" onClick={onClose}>
          <div
            className="modal-card-premium user-profile-card-premium"
            onClick={(e) => e.stopPropagation()}
          >
            {/* Header */}
            <div className="modal-header-premium">
              <div className="modal-header-left">
                <div className="modal-icon-badge">
                  <span>👤</span>
                </div>
                <div>
                  <h3 className="modal-title-premium">User Profile</h3>
                  <p className="modal-subtitle-premium">Contact Information & Avatar</p>
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

            {/* Modal Body */}
            <div className="modal-body-scrollable user-profile-body">
              {/* Avatar Centerpiece */}
              <div className="profile-avatar-centerpiece">
                <div
                  className="user-profile-avatar-container"
                  onClick={() => setIsZoomed(true)}
                  title="Click to enlarge profile picture"
                >
                  {avatarUrl ? (
                    <img
                      src={avatarUrl}
                      alt={user.displayName || user.username}
                      className="user-profile-large-img"
                      onError={() => setImageError(true)}
                    />
                  ) : (
                    <div className="user-profile-large-placeholder">{initials}</div>
                  )}

                  {/* Status Dot */}
                  <span className={`user-profile-online-dot ${isOnline ? 'online' : 'offline'}`} />

                  {/* Zoom hint on hover */}
                  <div className="avatar-zoom-overlay">
                    <span>🔍 Enlarge</span>
                  </div>
                </div>

                <div className="user-profile-header-text">
                  <h2 className="user-profile-fullname">
                    {user.displayName || user.username}
                  </h2>
                  <span className="user-profile-username-tag">@{user.username}</span>
                </div>
              </div>

              {/* User Details Grid */}
              <div className="user-info-section-grid">
                <div className="user-info-row">
                  <span className="info-row-icon">🟢</span>
                  <div className="info-row-text">
                    <span className="info-row-label">Status</span>
                    <span
                      className={`info-row-value ${
                        isOnline ? 'text-online' : 'text-offline'
                      }`}
                    >
                      {getLastSeenText()}
                    </span>
                  </div>
                </div>

                {user.email && (
                  <div className="user-info-row">
                    <span className="info-row-icon">📧</span>
                    <div className="info-row-text">
                      <span className="info-row-label">Email</span>
                      <span className="info-row-value">{user.email}</span>
                    </div>
                  </div>
                )}
              </div>
            </div>

            {/* Footer Actions */}
            <div className="modal-footer-premium justify-between">
              <button type="button" className="btn-modal-secondary" onClick={onClose}>
                Close
              </button>
              <button
                type="button"
                className="btn-modal-primary"
                onClick={handleSendMessage}
              >
                💬 Send Message
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Enlarged Photo Lightbox Modal */}
      {isZoomed && (
        <div
          className="modal-overlay-glass"
          onClick={() => setIsZoomed(false)}
        >
          <div
            className="avatar-enlarged-card"
            onClick={(e) => e.stopPropagation()}
          >
            {/* Top Close Button */}
            <button
              type="button"
              className="btn-lightbox-close-top"
              onClick={() => setIsZoomed(false)}
              aria-label="Close enlarged picture"
            >
              ✕
            </button>

            {/* Enlarged Image / Avatar Frame */}
            <div className="avatar-enlarged-frame">
              {avatarUrl ? (
                <img
                  src={avatarUrl}
                  alt={user.displayName || user.username}
                  className="avatar-enlarged-img"
                  onError={() => setImageError(true)}
                />
              ) : (
                <div className="avatar-enlarged-placeholder">{initials}</div>
              )}
            </div>

            {/* Bottom Caption */}
            <div className="avatar-enlarged-footer">
              <span className="avatar-enlarged-name">
                {user.displayName || user.username}
              </span>
              <span className="avatar-enlarged-handle">@{user.username}</span>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
