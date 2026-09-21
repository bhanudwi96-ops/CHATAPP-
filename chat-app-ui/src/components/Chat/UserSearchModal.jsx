import React, { useState, useEffect, useRef } from 'react';
import { userAPI } from '../../services/api';

const UserSearchModal = ({ isOpen, onClose, onSelectUser }) => {
  const [searchQuery, setSearchQuery] = useState('');
  const [users, setUsers] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const inputRef = useRef(null);

  useEffect(() => {
    if (isOpen) {
      setSearchQuery('');
      loadUsers('');
      setTimeout(() => inputRef.current?.focus(), 100);
    }
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen) return;

    const timer = setTimeout(() => {
      loadUsers(searchQuery);
    }, 300);

    return () => clearTimeout(timer);
  }, [searchQuery, isOpen]);

  const loadUsers = async (query) => {
    try {
      setLoading(true);
      setError('');
      const res = await userAPI.search(query);
      setUsers(res.data || []);
    } catch (err) {
      console.error('Failed to search users:', err);
      setError('Could not load users. Please try again.');
    } finally {
      setLoading(false);
    }
  };

  if (!isOpen) return null;

  return (
    <div className="modal-overlay-glass" onClick={onClose}>
      <div className="modal-card-premium user-search-modal" onClick={(e) => e.stopPropagation()}>
        {/* Header */}
        <div className="modal-header-premium">
          <div className="modal-header-left">
            <div className="modal-icon-badge">
              <span>🔍</span>
            </div>
            <div>
              <h3 className="modal-title-premium">Start Direct Chat</h3>
              <p className="modal-subtitle-premium">Search registered contacts to start chatting</p>
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

        <div className="modal-body-scrollable">
          {/* Search Input */}
          <div className="form-group-premium">
            <div className="input-with-icon-wrapper">
              <span className="input-prefix-icon">🔎</span>
              <input
                ref={inputRef}
                type="text"
                className="input-premium"
                placeholder="Search by name, @username, or email..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                autoFocus
              />
              {searchQuery && (
                <button
                  type="button"
                  className="input-clear-btn"
                  onClick={() => setSearchQuery('')}
                >
                  ✕
                </button>
              )}
            </div>
          </div>

          {error && (
            <div className="modal-alert-error">
              <span className="alert-icon">⚠️</span>
              <span>{error}</span>
            </div>
          )}

          {/* User Results */}
          <div className="users-picker-container" style={{ maxHeight: '320px' }}>
            {loading ? (
              <div className="picker-state-msg">
                <div className="spinner-glow" />
                <span>Searching contacts...</span>
              </div>
            ) : users.length === 0 ? (
              <div className="picker-state-msg">
                <span className="state-icon">👤</span>
                <span>No contacts found matching "{searchQuery}"</span>
              </div>
            ) : (
              users.map((user) => {
                const isOnline = user.status === 1;
                const initials = (user.displayName || user.username || 'U')
                  .split(' ')
                  .map((n) => n[0])
                  .join('')
                  .toUpperCase()
                  .substring(0, 2);

                return (
                  <div
                    key={user.id}
                    className="user-search-card"
                    onClick={() => onSelectUser(user)}
                  >
                    <div className="picker-avatar-wrapper">
                      {user.profilePictureUrl ? (
                        <img
                          src={user.profilePictureUrl}
                          alt={user.displayName}
                          className="user-avatar-img"
                        />
                      ) : (
                        <div className="user-avatar-placeholder">{initials}</div>
                      )}
                      <span className={`picker-status-dot ${isOnline ? 'online' : 'offline'}`} />
                    </div>

                    <div className="picker-user-text">
                      <div className="picker-user-name-line">
                        <span className="picker-name">{user.displayName}</span>
                        <span
                          className={`picker-badge ${
                            isOnline ? 'badge-online' : 'badge-offline'
                          }`}
                        >
                          {isOnline ? 'Online' : 'Offline'}
                        </span>
                      </div>
                      <span className="picker-handle">
                        @{user.username} {user.email && `• ${user.email}`}
                      </span>
                    </div>

                    <button type="button" className="start-chat-btn">
                      Chat 💬
                    </button>
                  </div>
                );
              })
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="modal-footer-premium">
          <button type="button" className="btn-modal-secondary" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
};

export default UserSearchModal;
