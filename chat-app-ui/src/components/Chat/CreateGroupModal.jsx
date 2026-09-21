import { useState, useEffect } from 'react';
import { userAPI, conversationAPI } from '../../services/api';
import signalRService from '../../services/signalr';

export default function CreateGroupModal({ isOpen, onClose, onGroupCreated, currentUser }) {
  const [groupName, setGroupName] = useState('');
  const [users, setUsers] = useState([]);
  const [selectedUserIds, setSelectedUserIds] = useState([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (isOpen) {
      setGroupName('');
      setSelectedUserIds([]);
      setSearchQuery('');
      setError('');
      fetchUsers();
    }
  }, [isOpen]);

  const fetchUsers = async () => {
    setLoading(true);
    try {
      const response = await userAPI.search('');
      const allUsers = Array.isArray(response.data) ? response.data : [];
      setUsers(allUsers.filter((u) => u.id !== currentUser?.id));
    } catch (err) {
      console.error('Error fetching users:', err);
    } finally {
      setLoading(false);
    }
  };

  if (!isOpen) return null;

  const toggleUserSelection = (userId) => {
    setSelectedUserIds((prev) =>
      prev.includes(userId) ? prev.filter((id) => id !== userId) : [...prev, userId]
    );
  };

  const filteredUsers = users.filter((u) => {
    const query = searchQuery.toLowerCase();
    return (
      (u.displayName && u.displayName.toLowerCase().includes(query)) ||
      (u.username && u.username.toLowerCase().includes(query))
    );
  });

  const handleCreateGroup = async (e) => {
    e.preventDefault();
    if (!groupName.trim()) {
      setError('Please enter a group name');
      return;
    }
    if (selectedUserIds.length === 0) {
      setError('Please select at least one member');
      return;
    }

    setSubmitting(true);
    setError('');

    try {
      const response = await conversationAPI.create({
        name: groupName.trim(),
        isGroupChat: true,
        participantIds: selectedUserIds,
      });

      const newGroup = response.data;
      signalRService.notifyGroupUpdated(newGroup.id);
      onGroupCreated(newGroup);
      onClose();
    } catch (err) {
      console.error('Error creating group:', err);
      setError(err.response?.data?.error || 'Failed to create group');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-overlay-glass" onClick={onClose}>
      <div
        className="modal-card-premium group-modal-premium"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="modal-header-premium">
          <div className="modal-header-left">
            <div className="modal-icon-badge">
              <span>👥</span>
            </div>
            <div>
              <h3 className="modal-title-premium">Create Group Chat</h3>
              <p className="modal-subtitle-premium">
                Collaborate with multiple members in real-time
              </p>
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

        <form onSubmit={handleCreateGroup} className="modal-form-wrapper">
          <div className="modal-body-scrollable">
            {error && (
              <div className="modal-alert-error">
                <span className="alert-icon">⚠️</span>
                <span>{error}</span>
              </div>
            )}

            {/* Group Name Field */}
            <div className="form-group-premium">
              <label className="form-label-premium">Group Name</label>
              <div className="input-with-icon-wrapper">
                <span className="input-prefix-icon">🏷️</span>
                <input
                  type="text"
                  className="input-premium"
                  placeholder="e.g. Design Team, Dev Sprint, Family"
                  value={groupName}
                  onChange={(e) => setGroupName(e.target.value)}
                  maxLength={50}
                  autoFocus
                />
              </div>
            </div>

            {/* Selected Member Chips */}
            {selectedUserIds.length > 0 && (
              <div className="selected-members-dock">
                <div className="selected-members-header">
                  <span>Selected Participants ({selectedUserIds.length})</span>
                  <button
                    type="button"
                    className="btn-clear-all"
                    onClick={() => setSelectedUserIds([])}
                  >
                    Clear All
                  </button>
                </div>
                <div className="selected-chips-grid">
                  {selectedUserIds.map((id) => {
                    const u = users.find((item) => item.id === id);
                    if (!u) return null;
                    return (
                      <span key={id} className="member-chip-premium">
                        <span className="chip-avatar-mini">
                          {u.displayName?.[0]?.toUpperCase() || 'U'}
                        </span>
                        <span className="chip-name">{u.displayName || u.username}</span>
                        <button
                          type="button"
                          className="chip-remove-btn"
                          onClick={() => toggleUserSelection(id)}
                        >
                          ✕
                        </button>
                      </span>
                    );
                  })}
                </div>
              </div>
            )}

            {/* Member Search Field */}
            <div className="form-group-premium">
              <label className="form-label-premium">Add Members</label>
              <div className="input-with-icon-wrapper">
                <span className="input-prefix-icon">🔍</span>
                <input
                  type="text"
                  className="input-premium"
                  placeholder="Search contacts by name or username..."
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
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

            {/* Users Picker List */}
            <div className="users-picker-container">
              {loading ? (
                <div className="picker-state-msg">
                  <div className="spinner-glow" />
                  <span>Loading contacts...</span>
                </div>
              ) : filteredUsers.length === 0 ? (
                <div className="picker-state-msg">
                  <span className="state-icon">👤</span>
                  <span>No matching contacts found</span>
                </div>
              ) : (
                filteredUsers.map((u) => {
                  const isSelected = selectedUserIds.includes(u.id);
                  const isOnline = u.status === 1;

                  return (
                    <div
                      key={u.id}
                      className={`picker-user-card ${isSelected ? 'active-selected' : ''}`}
                      onClick={() => toggleUserSelection(u.id)}
                    >
                      <div className="picker-avatar-wrapper">
                        <div className="picker-user-avatar-gradient">
                          {u.displayName?.[0]?.toUpperCase() ||
                            u.username?.[0]?.toUpperCase() ||
                            'U'}
                        </div>
                        <span className={`picker-status-dot ${isOnline ? 'online' : 'offline'}`} />
                      </div>

                      <div className="picker-user-text">
                        <div className="picker-user-name-line">
                          <span className="picker-name">{u.displayName || u.username}</span>
                          <span className={`picker-badge ${isOnline ? 'badge-online' : 'badge-offline'}`}>
                            {isOnline ? 'Online' : 'Offline'}
                          </span>
                        </div>
                        <span className="picker-handle">@{u.username}</span>
                      </div>

                      {/* Custom Modern Checkbox */}
                      <div className={`custom-check-pill ${isSelected ? 'checked' : ''}`}>
                        {isSelected && <span>✓</span>}
                      </div>
                    </div>
                  );
                })
              )}
            </div>
          </div>

          {/* Footer Actions */}
          <div className="modal-footer-premium">
            <button
              type="button"
              className="btn-modal-secondary"
              onClick={onClose}
            >
              Cancel
            </button>
            <button
              type="submit"
              className="btn-modal-primary"
              disabled={submitting || !groupName.trim() || selectedUserIds.length === 0}
            >
              {submitting ? (
                <>
                  <div className="btn-spinner" />
                  <span>Creating Group...</span>
                </>
              ) : (
                <>
                  <span>Create Group</span>
                  <span className="btn-counter-badge">{selectedUserIds.length}</span>
                </>
              )}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
