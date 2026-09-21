import { useState, useEffect, useRef } from 'react';
import { useAuth } from '../../context/AuthContext';
import { conversationAPI, API_BASE, getFullAvatarUrl } from '../../services/api';
import signalRService from '../../services/signalr';
import ConversationList from './ConversationList';
import ChatWindow from './ChatWindow';
import UserSearchModal from './UserSearchModal';
import CreateGroupModal from './CreateGroupModal';
import ProfileModal from './ProfileModal';
import UserProfileModal from './UserProfileModal';
import SupportModal from '../Support/SupportModal';
import './Chat.css';

export default function Chat() {
  const [conversations, setConversations] = useState([]);
  const [selectedConversation, setSelectedConversation] = useState(null);
  const [loading, setLoading] = useState(true);
  const [isUserSearchOpen, setIsUserSearchOpen] = useState(false);
  const [isCreateGroupOpen, setIsCreateGroupOpen] = useState(false);
  const [isProfileOpen, setIsProfileOpen] = useState(false);
  const [viewingUserProfile, setViewingUserProfile] = useState(null);
  const [isSupportOpen, setIsSupportOpen] = useState(false);
  const [theme, setTheme] = useState(() => localStorage.getItem('chatapp_theme') || 'dark');
  const { user, logout } = useAuth();
  const loadingRef = useRef(false);

  const toggleTheme = () => {
    const nextTheme = theme === 'dark' ? 'light' : 'dark';
    setTheme(nextTheme);
    localStorage.setItem('chatapp_theme', nextTheme);
  };

  // Setup real-time message & status listeners immediately on mount
  useEffect(() => {
    loadConversations();
    setupListeners();
  }, [user?.id]);

  // Keep selected conversation in sync when conversations list updates
  useEffect(() => {
    if (selectedConversation) {
      const updated = conversations.find((c) => c.id === selectedConversation.id);
      if (updated && updated !== selectedConversation) {
        setSelectedConversation(updated);
      }
    }
  }, [conversations]);

  const loadConversations = async () => {
    // Guard against double-call from React Strict Mode
    if (loadingRef.current) {
      console.log('[PERF-UI] loadConversations skipped (already in progress)');
      return;
    }
    loadingRef.current = true;
    try {
      const t0 = performance.now();
      const response = await conversationAPI.getAll();
      const elapsed = Math.round(performance.now() - t0);
      console.log(`[PERF-UI] loadConversations API call: ${elapsed}ms | Count: ${response.data.length}`);
      setConversations(response.data);
      setLoading(false);
    } catch (error) {
      console.error('❌ Error loading conversations:', error);
      setLoading(false);
    } finally {
      loadingRef.current = false;
    }
  };

  const setupListeners = () => {
    // Listen for users going online (1 = Online)
    signalRService.onUserOnline((data) => {
      const userId = data.userId || data.UserId;
      if (userId) {
        updateUserStatus(userId, 1, null);
      }
    });

    // Listen for users going offline (0 = Offline)
    signalRService.onUserOffline((data) => {
      const userId = data.userId || data.UserId;
      const lastSeen = data.lastSeen || data.LastSeen;
      if (userId) {
        updateUserStatus(userId, 0, lastSeen);
      }
    });

    // Listen for incoming message notifications for sidebar update
    const handleIncomingMessage = (message) => {
      if (!message) return;

      const convId = message.conversationId || message.ConversationId;
      const isCurrentChat = selectedConversation?.id === convId;

      setConversations((prevConvs) => {
        const exists = prevConvs.some((c) => c.id === convId);
        if (!exists) {
          loadConversations();
          return prevConvs;
        }

        return prevConvs.map((c) => {
          if (c.id === convId) {
            return {
              ...c,
              lastMessage: message,
              lastMessageAt: message.sentAt || new Date().toISOString(),
              unreadCount: isCurrentChat
                ? 0
                : (c.unreadCount || 0) + (message.senderId !== user?.id ? 1 : 0),
            };
          }
          return c;
        });
      });

      if (isCurrentChat && message.senderId !== user?.id) {
        signalRService.markConversationAsRead(convId);
      }
    };

    signalRService.onReceiveMessage(handleIncomingMessage);
    signalRService.onReceiveMessageNotification(handleIncomingMessage);

    // Listen for edited messages to update sidebar preview
    signalRService.onMessageEdited((updatedMessage) => {
      setConversations((prev) =>
        prev.map((c) => {
          if (c.id === updatedMessage.conversationId && c.lastMessage?.id === updatedMessage.id) {
            return {
              ...c,
              lastMessage: { ...c.lastMessage, ...updatedMessage },
            };
          }
          return c;
        })
      );
    });

    // Listen for deleted messages to update sidebar preview
    signalRService.onMessageDeleted((data) => {
      setConversations((prev) =>
        prev.map((c) => {
          if (c.id === data.conversationId && c.lastMessage?.id === data.messageId) {
            return {
              ...c,
              lastMessage: { ...c.lastMessage, isDeleted: true, content: 'This message was deleted' },
            };
          }
          return c;
        })
      );
    });

    // Listen for group creation / updates
    signalRService.onGroupUpdated(() => {
      loadConversations();
    });

    // Listen for conversation read events
    signalRService.onConversationRead((data) => {
      const convId = data.conversationId || data.ConversationId;
      if (convId) {
        setConversations((prev) =>
          prev.map((c) => (c.id === convId ? { ...c, unreadCount: 0 } : c))
        );
      }
    });
  };

  const updateUserStatus = (userId, status, lastSeen) => {
    setConversations((prevConversations) => {
      return prevConversations.map((conv) => ({
        ...conv,
        participants: (conv.participants || []).map((p) => {
          if (p.id === userId) {
            return {
              ...p,
              status: status,
              lastSeen: lastSeen || p.lastSeen,
            };
          }
          return p;
        }),
      }));
    });
  };

  const handleConversationSelect = (conversation) => {
    setConversations((prev) =>
      prev.map((c) => (c.id === conversation.id ? { ...c, unreadCount: 0 } : c))
    );

    setSelectedConversation({ ...conversation, unreadCount: 0 });
    signalRService.markConversationAsRead(conversation.id);
  };

  const handleSelectUserFromModal = async (selectedUser) => {
    setIsUserSearchOpen(false);
    setViewingUserProfile(null);

    try {
      const response = await conversationAPI.createPrivate(selectedUser.id);
      const newConversation = response.data;

      setConversations((prev) => {
        const exists = prev.some((c) => c.id === newConversation.id);
        if (exists) return prev;
        return [newConversation, ...prev];
      });

      setSelectedConversation(newConversation);
    } catch (error) {
      console.error('Error starting conversation:', error);
      alert('Failed to start conversation: ' + (error.response?.data?.error || error.message));
    }
  };

  const handleGroupCreated = (newGroup) => {
    setConversations((prev) => [newGroup, ...prev.filter((c) => c.id !== newGroup.id)]);
    setSelectedConversation(newGroup);
  };

  const handleGroupUpdated = (updatedGroup) => {
    setConversations((prev) =>
      prev.map((c) => (c.id === updatedGroup.id ? { ...c, ...updatedGroup } : c))
    );
    if (selectedConversation?.id === updatedGroup.id) {
      setSelectedConversation((prev) => ({ ...prev, ...updatedGroup }));
    }
  };

  const handleLeaveGroup = (conversationId) => {
    setConversations((prev) => prev.filter((c) => c.id !== conversationId));
    if (selectedConversation?.id === conversationId) {
      setSelectedConversation(null);
    }
  };

  const userAvatarUrl = getFullAvatarUrl(user?.profilePictureUrl);
  const initials = (user?.displayName || user?.username || 'U')
    .split(' ')
    .map((n) => n[0])
    .join('')
    .toUpperCase()
    .substring(0, 2);

  return (
    <div className="chat-container" data-theme={theme}>
      {/* Navbar */}
      <div className="chat-navbar">
        <div className="chat-brand-emblem">
          <div className="brand-glyph-halo">
            <span className="brand-glyph-icon">⚡</span>
          </div>
          <span className="brand-logo-text">Chat<span className="logo-accent">App</span></span>
          <span className={`signalr-status-badge ${signalRService.isConnected ? 'live' : 'connecting'}`}>
            <span className="pulse-dot"></span>
            {signalRService.isConnected ? 'LIVE' : 'SYNCING'}
          </span>
        </div>
        <div className="chat-user-info">
          {/* Theme Toggle Button */}
          <button
            type="button"
            className="theme-toggle-btn"
            onClick={toggleTheme}
            title={`Switch to ${theme === 'dark' ? 'Light' : 'Dark'} Mode`}
            aria-label="Toggle theme"
          >
            <span className="theme-toggle-icon">{theme === 'dark' ? '☀️' : '🌙'}</span>
            <span className="theme-toggle-text">{theme === 'dark' ? 'Light' : 'Dark'}</span>
          </button>

          {/* Clickable Profile Badge */}
          <div
            className="navbar-profile-badge"
            onClick={() => setIsProfileOpen(true)}
            title="Click to view and edit profile"
          >
            <div className="navbar-avatar-wrapper">
              {userAvatarUrl ? (
                <img
                  src={userAvatarUrl}
                  alt={user?.displayName}
                  className="navbar-avatar-img"
                  onError={(e) => {
                    e.currentTarget.style.display = 'none';
                    const fallback = e.currentTarget.nextElementSibling;
                    if (fallback) fallback.style.display = 'flex';
                  }}
                />
              ) : null}
              <div
                className="navbar-avatar-placeholder"
                style={{ display: userAvatarUrl ? 'none' : 'flex' }}
              >
                {initials}
              </div>
            </div>
            <div className="navbar-profile-meta">
              <span className="user-name">{user?.displayName || user?.username}</span>
              <span className="navbar-edit-hint">Edit Profile ⚙️</span>
            </div>
          </div>

          <button
            type="button"
            onClick={() => window.dispatchEvent(new CustomEvent('toggle-support-ai'))}
            className="navbar-ai-copilot-btn"
            title="Open ChatApp AI Copilot (Tickets & Support)"
            aria-label="Open AI Copilot"
          >
            <span className="copilot-icon-wrapper">
              <svg
                className="copilot-svg-icon"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M12 2a4 4 0 0 1 4 4v2H8V6a4 4 0 0 1 4-4z" />
                <rect width="18" height="12" x="3" y="8" rx="3" />
                <circle cx="9" cy="14" r="1.5" fill="currentColor" />
                <circle cx="15" cy="14" r="1.5" fill="currentColor" />
                <path d="M10 18h4" />
              </svg>
              <span className="copilot-status-dot" />
            </span>
            <span className="copilot-btn-text">AI Copilot</span>
          </button>

          <button onClick={logout} className="btn-logout">
            Logout
          </button>
        </div>
      </div>

      {/* Main chat area */}
      <div className="chat-main">
        {/* Sidebar with conversations */}
        <ConversationList
          conversations={conversations}
          selectedConversation={selectedConversation}
          onSelectConversation={handleConversationSelect}
          onNewConversation={() => setIsUserSearchOpen(true)}
          onNewGroup={() => setIsCreateGroupOpen(true)}
          onOpenUserProfile={(contact) => setViewingUserProfile(contact)}
          currentUser={user}
          loading={loading}
        />

        {/* Chat window */}
        {selectedConversation ? (
          <ChatWindow
            conversation={selectedConversation}
            currentUser={user}
            onGroupUpdated={handleGroupUpdated}
            onLeaveGroup={handleLeaveGroup}
            onOpenUserProfile={(contact) => setViewingUserProfile(contact)}
          />
        ) : (
          <div className="chat-empty">
            <div className="empty-state">
              <div className="empty-state-emblem">
                <div className="emblem-pulse-ring"></div>
                <div className="emblem-core">
                  <span>⚡</span>
                </div>
              </div>
              <h2>Welcome to <span className="logo-accent">ChatApp</span></h2>
              <p>Experience ultra-fast real-time messaging. Select an active chat or launch a new conversation.</p>
              <div className="empty-state-actions">
                <button
                  type="button"
                  className="btn-new-chat-hero"
                  onClick={() => setIsUserSearchOpen(true)}
                >
                  ⚡ Direct Chat
                </button>
                <button
                  type="button"
                  className="btn-new-group-hero"
                  onClick={() => setIsCreateGroupOpen(true)}
                >
                  👥 Create Group
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {/* User Search & Contact Picker Modal */}
      <UserSearchModal
        isOpen={isUserSearchOpen}
        onClose={() => setIsUserSearchOpen(false)}
        onSelectUser={handleSelectUserFromModal}
      />

      {/* Create Group Modal */}
      <CreateGroupModal
        isOpen={isCreateGroupOpen}
        onClose={() => setIsCreateGroupOpen(false)}
        onGroupCreated={handleGroupCreated}
        currentUser={user}
      />

      {/* Current User Edit Profile & DP Settings Modal */}
      <ProfileModal
        isOpen={isProfileOpen}
        onClose={() => {
          setIsProfileOpen(false);
          loadConversations();
        }}
      />

      {/* Contact Profile & DP Viewer Modal */}
      <UserProfileModal
        user={viewingUserProfile}
        isOpen={!!viewingUserProfile}
        onClose={() => setViewingUserProfile(null)}
        onStartChat={handleSelectUserFromModal}
      />

      {/* Customer Support Modal */}
      <SupportModal
        isOpen={isSupportOpen}
        onClose={() => setIsSupportOpen(false)}
      />
    </div>
  );
}
