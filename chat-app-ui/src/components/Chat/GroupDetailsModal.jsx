import { useState, useEffect } from 'react';
import { conversationAPI, userAPI } from '../../services/api';
import signalRService from '../../services/signalr';

export default function GroupDetailsModal({
  isOpen,
  onClose,
  conversation,
  currentUser,
  onGroupUpdated,
  onLeaveGroup,
}) {
  const [isAddingMembers, setIsAddingMembers] = useState(false);
  const [availableUsers, setAvailableUsers] = useState([]);
  const [selectedToAdd, setSelectedToAdd] = useState([]);
  const [loadingUsers, setLoadingUsers] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [error, setError] = useState('');

  const participants = conversation?.participants || [];
  const currentParticipant = participants.find((p) => p.id === currentUser?.id);
  const isCurrentUserAdmin = currentParticipant?.isAdmin === true;

  useEffect(() => {
    if (isOpen) {
      setIsAddingMembers(false);
      setSelectedToAdd([]);
      setError('');
    }
  }, [isOpen]);

  const loadAvailableUsers = async () => {
    setLoadingUsers(true);
    try {
      const response = await userAPI.search('');
      const all = Array.isArray(response.data) ? response.data : [];
      const existingIds = new Set(participants.map((p) => p.id));
      setAvailableUsers(all.filter((u) => !existingIds.has(u.id)));
      setIsAddingMembers(true);
    } catch (err) {
      console.error('Error fetching available users:', err);
    } finally {
      setLoadingUsers(false);
    }
  };

  const handleAddMembersSubmit = async () => {
    if (selectedToAdd.length === 0) return;
    setActionLoading(true);
    setError('');

    try {
      const response = await conversationAPI.addParticipants(conversation.id, selectedToAdd);
      signalRService.notifyGroupUpdated(conversation.id);
      onGroupUpdated(response.data);
      setIsAddingMembers(false);
      setSelectedToAdd([]);
    } catch (err) {
      console.error('Error adding members:', err);
      setError(err.response?.data?.error || 'Failed to add members');
    } finally {
      setActionLoading(false);
    }
  };

  const handleRemoveMember = async (userId, memberName) => {
    if (!window.confirm(`Are you sure you want to remove ${memberName} from this group?`)) {
      return;
    }
    setActionLoading(true);
    setError('');

    try {
      await conversationAPI.removeParticipant(conversation.id, userId);
      signalRService.notifyGroupUpdated(conversation.id);
      const updated = {
        ...conversation,
        participants: participants.filter((p) => p.id !== userId),
      };
      onGroupUpdated(updated);
    } catch (err) {
      console.error('Error removing member:', err);
      setError(err.response?.data?.error || 'Failed to remove member');
    } finally {
      setActionLoading(false);
    }
  };

  const handleToggleAdmin = async (userId, currentAdminStatus, memberName) => {
    const newStatus = !currentAdminStatus;
    const action = newStatus ? 'promote to Admin' : 'dismiss as Admin';
    if (!window.confirm(`Are you sure you want to ${action} ${memberName}?`)) {
      return;
    }
    setActionLoading(true);
    setError('');

    try {
      await conversationAPI.setAdmin(conversation.id, userId, newStatus);
      signalRService.notifyGroupUpdated(conversation.id);
      const updated = {
        ...conversation,
        participants: participants.map((p) =>
          p.id === userId ? { ...p, isAdmin: newStatus } : p
        ),
      };
      onGroupUpdated(updated);
    } catch (err) {
      console.error('Error updating admin status:', err);
      setError(err.response?.data?.error || 'Failed to update admin permissions');
    } finally {
      setActionLoading(false);
    }
  };

  const handleLeaveGroup = async () => {
    if (!window.confirm('Are you sure you want to leave this group?')) {
      return;
    }
    setActionLoading(true);
    try {
      await conversationAPI.leave(conversation.id);
      signalRService.notifyGroupUpdated(conversation.id);
      onLeaveGroup(conversation.id);
      onClose();
    } catch (err) {
      console.error('Error leaving group:', err);
      setError(err.response?.data?.error || 'Failed to leave group');
    } finally {
      setActionLoading(false);
    }
  };

  if (!isOpen || !conversation) return null;

  return (
    <div className="modal-overlay-glass" onClick={onClose}>
      <div
        className="modal-card-premium group-details-card-premium"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="modal-header-premium">
          <div className="modal-header-left">
            <div className="modal-icon-badge group-badge-gradient">
              <span>👥</span>
            </div>
            <div>
              <h3 className="modal-title-premium">{conversation.name || 'Group Settings'}</h3>
              <p className="modal-subtitle-premium">
                {participants.length} total participants
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

        <div className="modal-body-scrollable">
          {error && (
            <div className="modal-alert-error">
              <span className="alert-icon">⚠️</span>
              <span>{error}</span>
            </div>
          )}

          {/* Admin Add Members Action Button */}
          {isCurrentUserAdmin && !isAddingMembers && (
            <button
              type="button"
              className="btn-add-members-hero"
              onClick={loadAvailableUsers}
              disabled={actionLoading || loadingUsers}
            >
              <span className="plus-icon">➕</span>
              <span>Add New Members to Group</span>
            </button>
          )}

          {/* Add Members Drawer */}
          {isAddingMembers && (
            <div className="add-members-panel-premium">
              <div className="add-members-panel-top">
                <h4>Select Contacts to Add</h4>
                <button
                  type="button"
                  className="btn-text-cancel"
                  onClick={() => setIsAddingMembers(false)}
                >
                  Cancel
                </button>
              </div>

              <div className="add-members-scroll-area">
                {loadingUsers ? (
                  <div className="picker-state-msg">
                    <div className="spinner-glow" />
                    <span>Loading available contacts...</span>
                  </div>
                ) : availableUsers.length === 0 ? (
                  <div className="picker-state-msg">
                    <span>All contacts are already in this group</span>
                  </div>
                ) : (
                  availableUsers.map((u) => {
                    const isSelected = selectedToAdd.includes(u.id);
                    return (
                      <div
                        key={u.id}
                        className={`picker-user-card ${isSelected ? 'active-selected' : ''}`}
                        onClick={() => {
                          setSelectedToAdd((prev) =>
                            prev.includes(u.id)
                              ? prev.filter((id) => id !== u.id)
                              : [...prev, u.id]
                          );
                        }}
                      >
                        <div className="picker-avatar-wrapper">
                          <div className="picker-user-avatar-gradient">
                            {u.displayName?.[0]?.toUpperCase() || 'U'}
                          </div>
                        </div>
                        <div className="picker-user-text">
                          <span className="picker-name">{u.displayName || u.username}</span>
                          <span className="picker-handle">@{u.username}</span>
                        </div>
                        <div className={`custom-check-pill ${isSelected ? 'checked' : ''}`}>
                          {isSelected && <span>✓</span>}
                        </div>
                      </div>
                    );
                  })
                )}
              </div>

              <button
                type="button"
                className="btn-modal-primary"
                style={{ width: '100%', marginTop: '12px' }}
                disabled={selectedToAdd.length === 0 || actionLoading}
                onClick={handleAddMembersSubmit}
              >
                {actionLoading ? 'Adding...' : `Add Selected (${selectedToAdd.length})`}
              </button>
            </div>
          )}

          {/* Members List */}
          <div className="members-section-premium">
            <div className="section-title-row">
              <span className="section-heading">Group Members</span>
              <span className="section-counter">{participants.length}</span>
            </div>

            <div className="members-grid-premium">
              {participants.map((p) => {
                const isMe = p.id === currentUser?.id;
                const isOnline = p.status === 1;

                return (
                  <div key={p.id} className="member-card-premium">
                    <div className="member-avatar-container">
                      <div className="member-avatar-gradient">
                        {p.displayName?.[0]?.toUpperCase() ||
                          p.username?.[0]?.toUpperCase() ||
                          'U'}
                      </div>
                      <span className={`member-online-dot ${isOnline ? 'online' : 'offline'}`} />
                    </div>

                    <div className="member-info-column">
                      <div className="member-name-badges">
                        <span className="member-full-name">
                          {p.displayName || p.username}
                        </span>
                        {isMe && <span className="badge-you">You</span>}
                        {p.isAdmin && <span className="badge-admin-crown">👑 Admin</span>}
                      </div>
                      <span className="member-status-label">
                        {isOnline ? 'Active Now' : 'Offline'}
                      </span>
                    </div>

                    {/* Admin Action Buttons */}
                    {isCurrentUserAdmin && !isMe && (
                      <div className="member-admin-actions">
                        <button
                          type="button"
                          className="btn-admin-icon"
                          title={p.isAdmin ? 'Dismiss Admin' : 'Make Admin'}
                          onClick={() =>
                            handleToggleAdmin(p.id, p.isAdmin, p.displayName || p.username)
                          }
                          disabled={actionLoading}
                        >
                          {p.isAdmin ? '👑' : '⭐'}
                        </button>
                        <button
                          type="button"
                          className="btn-admin-icon danger"
                          title="Remove from group"
                          onClick={() =>
                            handleRemoveMember(p.id, p.displayName || p.username)
                          }
                          disabled={actionLoading}
                        >
                          🗑️
                        </button>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        </div>

        {/* Footer */}
        <div className="modal-footer-premium justify-between">
          <button
            type="button"
            className="btn-danger-leave"
            onClick={handleLeaveGroup}
            disabled={actionLoading}
          >
            🚪 Leave Group
          </button>
          <button type="button" className="btn-modal-secondary" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>
  );
}
