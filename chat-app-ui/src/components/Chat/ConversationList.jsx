import { getFullAvatarUrl } from '../../services/api';
import './Chat.css';

export default function ConversationList({
  conversations,
  selectedConversation,
  onSelectConversation,
  onNewConversation,
  onNewGroup,
  onOpenUserProfile,
  currentUser,
  loading,
}) {
  const currentUserId = currentUser?.id || JSON.parse(sessionStorage.getItem('user') || '{}')?.id;

  const formatTime = (dateString) => {
    if (!dateString) return '';
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'Just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffMins < 1440) return `${Math.floor(diffMins / 60)}h ago`;
    return date.toLocaleDateString();
  };

  const getConversationName = (conversation) => {
    if (conversation.isGroupChat) {
      return conversation.name || 'Group Chat';
    }

    const otherUser = conversation.participants?.find(
      (p) => p.id !== currentUserId
    );
    return otherUser?.displayName || otherUser?.username || 'Unknown';
  };

  const getOtherUser = (conversation) => {
    if (conversation.isGroupChat) return null;
    return conversation.participants?.find((p) => p.id !== currentUserId);
  };

  const getLastSeenText = (user) => {
    if (!user) return '';

    if (user.status === 1) {
      return 'Active now';
    }

    if (!user.lastSeen) return 'Offline';

    const lastSeen = new Date(user.lastSeen);
    const now = new Date();
    const diffMs = now - lastSeen;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'Active just now';
    if (diffMins < 60) return `Active ${diffMins}m ago`;
    if (diffMins < 1440) return `Active ${Math.floor(diffMins / 60)}h ago`;
    return 'Offline';
  };

  const getLastMessagePreview = (conversation) => {
    if (!conversation.lastMessage) return 'No messages yet';
    const content = conversation.lastMessage.isDeleted
      ? '🚫 This message was deleted'
      : conversation.lastMessage.content;
    return (
      (content?.substring(0, 35) || '') +
      ((content?.length || 0) > 35 ? '...' : '')
    );
  };

  if (loading) {
    return (
      <div className="conversation-sidebar">
        <div className="sidebar-header">
          <h2>Conversations</h2>
          <div className="sidebar-header-actions">
            <button className="btn-new-chat" disabled>
              + Chat
            </button>
            <button className="btn-new-group" disabled>
              + Group
            </button>
          </div>
        </div>
        <div className="conversation-loading">Loading conversations...</div>
      </div>
    );
  }

  return (
    <div className="conversation-sidebar">
      <div className="sidebar-header">
        <h2>Chats</h2>
        <div className="sidebar-header-actions">
          <button className="btn-new-chat" onClick={onNewConversation} title="New Direct Chat">
            + Direct
          </button>
          <button className="btn-new-group" onClick={onNewGroup} title="Create Group Chat">
            👥 + Group
          </button>
        </div>
      </div>

      <div className="conversation-list">
        {conversations.length === 0 ? (
          <div className="no-conversations">
            <p>No conversations yet</p>
            <p className="hint">Click "+ Direct" or "👥 + Group" to start</p>
          </div>
        ) : (
          conversations.map((conversation) => {
            const isGroup = conversation.isGroupChat;
            const otherUser = getOtherUser(conversation);
            const isOnline = !isGroup && otherUser?.status === 1;
            const avatarUrl = !isGroup ? getFullAvatarUrl(otherUser?.profilePictureUrl) : null;
            const initial = getConversationName(conversation).charAt(0).toUpperCase();

            return (
              <div
                key={conversation.id}
                className={`conversation-item ${
                  selectedConversation?.id === conversation.id ? 'active' : ''
                } ${isGroup ? 'group-item' : ''}`}
                onClick={() => onSelectConversation(conversation)}
              >
                {/* Avatar Container with independent click for DP & Profile preview */}
                <div
                  className="conversation-avatar-container"
                  onClick={(e) => {
                    if (!isGroup && otherUser && onOpenUserProfile) {
                      e.stopPropagation();
                      onOpenUserProfile(otherUser);
                    }
                  }}
                  title={!isGroup ? 'Click to view profile & DP' : 'Group'}
                >
                  <div className={`conversation-avatar ${isGroup ? 'group-avatar' : ''}`}>
                    {isGroup ? (
                      '👥'
                    ) : avatarUrl ? (
                      <>
                        <img
                          src={avatarUrl}
                          alt={getConversationName(conversation)}
                          onError={(e) => {
                            e.currentTarget.style.display = 'none';
                            const fallback = e.currentTarget.nextElementSibling;
                            if (fallback) fallback.style.display = 'block';
                          }}
                        />
                        <span style={{ display: 'none' }}>{initial}</span>
                      </>
                    ) : (
                      initial
                    )}
                  </div>
                  {isOnline && <div className="online-indicator"></div>}
                </div>

                <div className="conversation-info">
                  <div className="conversation-header">
                    <h3 className="conversation-name">
                      {getConversationName(conversation)}
                    </h3>
                    <span className="conversation-time">
                      {formatTime(conversation.lastMessageAt)}
                    </span>
                  </div>

                  <div className="conversation-preview">
                    <p>{getLastMessagePreview(conversation)}</p>
                    {conversation.unreadCount > 0 && (
                      <span className="unread-badge">{conversation.unreadCount}</span>
                    )}
                  </div>

                  {!isGroup && otherUser && (
                    <div className="last-seen-status">{getLastSeenText(otherUser)}</div>
                  )}

                  {isGroup && (
                    <div className="group-participant-count">
                      {(conversation.participants || []).length} participants
                    </div>
                  )}
                </div>
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
