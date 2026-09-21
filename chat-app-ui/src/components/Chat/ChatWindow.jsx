import { useState, useEffect, useRef } from 'react';
import { messageAPI, uploadAPI, API_BASE, getFullAvatarUrl } from '../../services/api';
import signalRService from '../../services/signalr';
import { useAuth } from '../../context/AuthContext';
import EmojiPicker from './EmojiPicker';
import GroupDetailsModal from './GroupDetailsModal';
import './Chat.css';

const QUICK_REACTIONS = ['👍', '❤️', '😂', '😮', '😢', '🔥'];

export default function ChatWindow({
  conversation,
  onGroupUpdated,
  onLeaveGroup,
  onOpenUserProfile,
}) {
  const [messages, setMessages] = useState([]);
  const [newMessage, setNewMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [typingUsers, setTypingUsers] = useState([]);
  const [selectedFile, setSelectedFile] = useState(null);
  const [filePreview, setFilePreview] = useState(null);
  const [uploading, setUploading] = useState(false);
  const [showEmojiPicker, setShowEmojiPicker] = useState(false);
  const [lightboxImage, setLightboxImage] = useState(null);

  // Feature 4: Edit, Delete, Reply states
  const [replyingTo, setReplyingTo] = useState(null);
  const [editingMessageId, setEditingMessageId] = useState(null);
  const [editContent, setEditContent] = useState('');
  const [activeDropdownMsgId, setActiveDropdownMsgId] = useState(null);

  // Close message dropdown on outside click
  useEffect(() => {
    const handleOutsideClick = () => setActiveDropdownMsgId(null);
    document.addEventListener('click', handleOutsideClick);
    return () => document.removeEventListener('click', handleOutsideClick);
  }, []);

  // Feature 5: Group details & @Mentions states
  const [isGroupModalOpen, setIsGroupModalOpen] = useState(false);
  const [mentionQuery, setMentionQuery] = useState(null);
  const [mentionIndex, setMentionIndex] = useState(0);

  const messagesEndRef = useRef(null);
  const fileInputRef = useRef(null);
  const typingTimeoutRef = useRef(null);
  const messageInputRef = useRef(null);
  const { user } = useAuth();

  const isGroup = conversation?.isGroupChat;
  const participants = conversation?.participants || [];
  const currentParticipant = participants.find((p) => p.id === user?.id);
  const isCurrentUserAdmin = currentParticipant?.isAdmin === true;
  const otherUser = !isGroup ? participants.find((p) => p.id !== user?.id) : null;

  const otherAvatarUrl = otherUser ? getFullAvatarUrl(otherUser.profilePictureUrl) : null;
  const conversationTitle = isGroup
    ? conversation?.name || 'Group Chat'
    : otherUser?.displayName || otherUser?.username || 'Unknown';
  const otherInitials = (conversationTitle || 'U').charAt(0).toUpperCase();

  // Load messages when conversation changes
  useEffect(() => {
    if (conversation) {
      loadMessages();
      joinConversation();
      markAsRead();
      setSelectedFile(null);
      setFilePreview(null);
      setShowEmojiPicker(false);
      setReplyingTo(null);
      setEditingMessageId(null);
      setMentionQuery(null);
    }

    return () => {
      if (conversation) {
        leaveConversation();
      }
    };
  }, [conversation?.id]);

  // Keep input focused whenever active conversation is selected/changed
  useEffect(() => {
    if (conversation?.id) {
      const focusTimer = setTimeout(() => {
        messageInputRef.current?.focus();
      }, 60);
      return () => clearTimeout(focusTimer);
    }
  }, [conversation?.id]);

  // Set up SignalR listeners
  useEffect(() => {
    // 1. Listen for new messages
    signalRService.onReceiveMessage((message) => {
      if (message.conversationId === conversation?.id) {
        setMessages((prev) => {
          const filtered = prev.filter(
            (m) =>
              !(
                m.isOptimistic &&
                m.senderId === message.senderId &&
                m.content === message.content
              )
          );
          const exists = filtered.some((m) => m.id === message.id);
          if (exists) return filtered;
          return [...filtered, message];
        });
        scrollToBottom();

        if (message.senderId !== user?.id) {
          signalRService.markConversationAsRead(conversation.id);
        }
      }
    });

    // 2. Listen for edited messages
    signalRService.onMessageEdited((updatedMessage) => {
      if (updatedMessage.conversationId === conversation?.id) {
        setMessages((prev) =>
          prev.map((m) => (m.id === updatedMessage.id ? { ...m, ...updatedMessage } : m))
        );
      }
    });

    // 3. Listen for deleted messages
    signalRService.onMessageDeleted((data) => {
      if (data.conversationId === conversation?.id) {
        setMessages((prev) =>
          prev.map((m) =>
            m.id === data.messageId
              ? {
                  ...m,
                  isDeleted: true,
                  content: 'This message was deleted',
                  attachmentUrl: null,
                  reactions: [],
                }
              : m
          )
        );
      }
    });

    // 4. Listen for real-time reaction updates
    signalRService.onMessageReactionUpdated((data) => {
      if (data.conversationId?.toLowerCase() === conversation?.id?.toLowerCase()) {
        setMessages((prev) =>
          prev.map((msg) => {
            if (msg.id?.toLowerCase() === data.messageId?.toLowerCase()) {
              const currentUserId = user?.id?.toLowerCase();
              const updatedReactions = (data.reactions || []).map((r) => ({
                ...r,
                hasReacted: (r.userIds || []).some(
                  (uid) => uid?.toLowerCase() === currentUserId
                ),
              }));
              return { ...msg, reactions: updatedReactions };
            }
            return msg;
          })
        );
      }
    });

    // 5. Listen for typing indicators
    signalRService.onUserTyping((data) => {
      if (data.conversationId === conversation?.id && data.userId !== user?.id) {
        if (data.isTyping) {
          setTypingUsers((prev) => [...new Set([...prev, data.username])]);
        } else {
          setTypingUsers((prev) => prev.filter((u) => u !== data.username));
        }
      }
    });
  }, [conversation?.id, user?.id]);

  // Scroll to bottom when messages change
  useEffect(() => {
    scrollToBottom();
  }, [messages.length]);

  const loadMessages = async () => {
    setLoading(true);
    try {
      const response = await messageAPI.getConversationMessages(conversation.id);
      const mapped = response.data.reverse().map((msg) => ({
        ...msg,
        reactions: (msg.reactions || []).map((r) => ({
          ...r,
          hasReacted: (r.userIds || []).includes(user?.id),
        })),
      }));
      setMessages(mapped);
      setLoading(false);
      scrollToBottom();
      markAsRead();
    } catch (error) {
      console.error('Error loading messages:', error);
      setLoading(false);
    }
  };

  const markAsRead = async () => {
    if (!conversation?.id) return;
    try {
      signalRService.markConversationAsRead(conversation.id);
    } catch (err) {}
  };

  const joinConversation = async () => {
    try {
      await signalRService.joinConversation(conversation.id);
    } catch (error) {
      console.error('Error joining conversation:', error);
    }
  };

  const leaveConversation = async () => {
    try {
      await signalRService.leaveConversation(conversation.id);
    } catch (error) {
      console.error('Error leaving conversation:', error);
    }
  };

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  const scrollToMessage = (messageId) => {
    if (!messageId) return;
    const element = document.getElementById(`msg-${messageId}`);
    if (element) {
      element.scrollIntoView({ behavior: 'smooth', block: 'center' });
      element.classList.add('message-highlight-pulse');
      setTimeout(() => {
        element.classList.remove('message-highlight-pulse');
      }, 2000);
    }
  };

  // Handle file selection
  const handleFileSelect = (e) => {
    const file = e.target.files?.[0];
    if (!file) return;

    if (file.size > 25 * 1024 * 1024) {
      alert('File size exceeds maximum limit of 25MB');
      return;
    }

    setSelectedFile(file);

    if (file.type.startsWith('image/')) {
      const reader = new FileReader();
      reader.onload = (event) => {
        setFilePreview({
          type: 'image',
          name: file.name,
          size: (file.size / 1024).toFixed(1) + ' KB',
          url: event.target?.result,
        });
      };
      reader.readAsDataURL(file);
    } else {
      setFilePreview({
        type: 'file',
        name: file.name,
        size: (file.size / 1024).toFixed(1) + ' KB',
      });
    }
  };

  const removeSelectedFile = () => {
    setSelectedFile(null);
    setFilePreview(null);
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  };

  // Feature 4: Send Message with optional Quoted Reply
  const handleSendMessage = async (e) => {
    e.preventDefault();
    const content = newMessage.trim();
    if (!content && !selectedFile) return;

    setSending(true);
    let attachmentUrl = null;
    let fileType = 0; // 0 = Text, 1 = Image, 2 = File
    let fileName = null;
    let fileSize = null;
    const replyTo = replyingTo;

    if (selectedFile) {
      try {
        setUploading(true);
        const uploadRes = await uploadAPI.uploadFile(selectedFile);
        attachmentUrl = uploadRes.data.url;
        fileName = uploadRes.data.fileName;
        fileSize = uploadRes.data.fileSize;
        fileType = uploadRes.data.fileType === 'image' ? 1 : 2;
      } catch (uploadErr) {
        console.error('Upload failed:', uploadErr);
        alert('Failed to upload attachment. Please try again.');
        setUploading(false);
        setSending(false);
        return;
      } finally {
        setUploading(false);
      }
    }

    // 1. Optimistic message
    const tempId = `temp-${Date.now()}-${Math.random()}`;
    const optimisticMsg = {
      id: tempId,
      conversationId: conversation.id,
      senderId: user?.id,
      senderName: user?.displayName,
      content: content || (fileType === 1 ? 'Sent an image' : fileName || 'Sent a file'),
      type: fileType,
      attachmentUrl: attachmentUrl,
      fileName: fileName,
      fileSize: fileSize,
      sentAt: new Date().toISOString(),
      isRead: false,
      isOptimistic: true,
      replyToMessageId: replyTo?.id || null,
      replyToSenderName: replyTo?.senderName || null,
      replyToContent: replyTo?.content || null,
      reactions: [],
    };

    setMessages((prev) => [...prev, optimisticMsg]);
    setNewMessage('');
    setReplyingTo(null);
    removeSelectedFile();
    setShowEmojiPicker(false);
    setMentionQuery(null);
    handleTypingEnd();
    scrollToBottom();
    setTimeout(() => messageInputRef.current?.focus(), 0);

    // 2. Dispatch via SignalR
    try {
      await signalRService.sendMessage(
        conversation.id,
        content || (fileType === 1 ? 'Sent an image' : fileName || 'Sent a file'),
        fileType,
        attachmentUrl,
        fileName,
        fileSize,
        replyTo?.id || null
      );
    } catch (error) {
      console.error('Error sending message:', error);
      setMessages((prev) => prev.filter((m) => m.id !== tempId));
      alert('Failed to send message: ' + (error.message || 'Network error'));
    } finally {
      setSending(false);
      setTimeout(() => messageInputRef.current?.focus(), 0);
    }
  };

  // Feature 4: Message Edit Handlers
  const handleStartEdit = (message) => {
    setEditingMessageId(message.id);
    setEditContent(message.content);
  };

  const handleCancelEdit = () => {
    setEditingMessageId(null);
    setEditContent('');
  };

  const handleSaveEdit = async (messageId) => {
    if (!editContent.trim()) return;

    try {
      await messageAPI.edit(messageId, editContent.trim());
      signalRService.editMessage(messageId, editContent.trim());

      setMessages((prev) =>
        prev.map((m) =>
          m.id === messageId
            ? { ...m, content: editContent.trim(), isEdited: true, editedAt: new Date().toISOString() }
            : m
        )
      );
      setEditingMessageId(null);
      setEditContent('');
    } catch (err) {
      console.error('Error editing message:', err);
      alert('Failed to edit message: ' + (err.response?.data?.error || err.message));
    }
  };

  // Feature 4: Message Delete Handler
  const handleDeleteMessage = async (messageId) => {
    if (!window.confirm('Are you sure you want to delete this message for everyone?')) {
      return;
    }

    try {
      await messageAPI.delete(messageId);
      signalRService.deleteMessage(messageId);

      setMessages((prev) =>
        prev.map((m) =>
          m.id === messageId
            ? {
                ...m,
                isDeleted: true,
                content: 'This message was deleted',
                attachmentUrl: null,
                reactions: [],
              }
            : m
        )
      );
    } catch (err) {
      console.error('Error deleting message:', err);
      alert('Failed to delete message: ' + (err.response?.data?.error || err.message));
    }
  };

  const handleToggleReaction = async (messageId, emoji) => {
    try {
      await signalRService.reactToMessage(messageId, emoji);
    } catch (err) {
      console.error('Reaction error:', err);
    }
  };

  const handleInsertEmoji = (emoji) => {
    setNewMessage((prev) => prev + emoji);
    messageInputRef.current?.focus();
  };

  const handleTypingStart = () => {
    signalRService.sendTyping(conversation.id, true);

    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current);
    }

    typingTimeoutRef.current = setTimeout(() => {
      handleTypingEnd();
    }, 3000);
  };

  const handleTypingEnd = () => {
    signalRService.sendTyping(conversation.id, false);
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current);
    }
  };

  // Feature 5: @Mentions Autocomplete Detection
  const handleInputChange = (e) => {
    const val = e.target.value;
    setNewMessage(val);
    handleTypingStart();

    if (isGroup) {
      const cursor = e.target.selectionStart;
      const textBeforeCursor = val.slice(0, cursor);
      const lastAt = textBeforeCursor.lastIndexOf('@');

      if (lastAt !== -1 && (lastAt === 0 || /\s/.test(textBeforeCursor[lastAt - 1]))) {
        const query = textBeforeCursor.slice(lastAt + 1);
        if (!query.includes(' ')) {
          setMentionQuery(query);
          setMentionIndex(lastAt);
          return;
        }
      }
    }
    setMentionQuery(null);
  };

  const handleSelectMention = (participant) => {
    const tag = `@${participant.displayName || participant.username} `;
    const before = newMessage.slice(0, mentionIndex);
    const after = newMessage.slice(messageInputRef.current?.selectionStart || mentionIndex);
    setNewMessage(before + tag + after);
    setMentionQuery(null);
    messageInputRef.current?.focus();
  };

  const mentionCandidates = isGroup && mentionQuery !== null
    ? participants.filter((p) => {
        if (p.id === user?.id) return false;
        const q = mentionQuery.toLowerCase();
        return (
          (p.displayName && p.displayName.toLowerCase().includes(q)) ||
          (p.username && p.username.toLowerCase().includes(q))
        );
      })
    : [];

  const formatMessageTime = (dateString) => {
    const date = new Date(dateString);
    return date.toLocaleTimeString('en-US', {
      hour: '2-digit',
      minute: '2-digit',
    });
  };

  const getFullAttachmentUrl = (url) => {
    if (!url) return '';
    if (url.startsWith('http://') || url.startsWith('https://')) return url;
    return `${API_BASE}${url}`;
  };

  const getConversationName = () => {
    if (conversation.isGroupChat) {
      return conversation.name || 'Group Chat';
    }
    const otherUser = conversation.participants.find((p) => p.id !== user?.id);
    return otherUser?.displayName || 'Unknown';
  };

  const getOtherUser = () => {
    if (conversation.isGroupChat) return null;
    return conversation.participants.find((p) => p.id !== user?.id);
  };

  const getLastSeenStatus = () => {
    const otherUser = getOtherUser();
    if (!otherUser || conversation.isGroupChat) return null;

    if (otherUser.status === 1) {
      return { text: 'Active now', isOnline: true };
    }

    if (!otherUser.lastSeen) return { text: 'Offline', isOnline: false };

    const lastSeen = new Date(otherUser.lastSeen);
    const now = new Date();
    const diffMs = now - lastSeen;
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return { text: 'Active just now', isOnline: false };
    if (diffMins < 60) return { text: `Active ${diffMins}m ago`, isOnline: false };
    if (diffMins < 1440) return { text: `Active ${Math.floor(diffMins / 60)}h ago`, isOnline: false };
    return { text: 'Offline', isOnline: false };
  };

  // Helper: Format message text and highlight @mentions
  const renderMessageContent = (content) => {
    if (!content) return null;
    const parts = content.split(/(@[a-zA-Z0-9_.\s]+?)(?=\s|$|[.,!?])/g);

    return parts.map((part, idx) => {
      if (part.startsWith('@')) {
        const isMentioningMe =
          user?.displayName && part.toLowerCase().includes(user.displayName.toLowerCase());
        return (
          <span
            key={idx}
            className={`mention-pill ${isMentioningMe ? 'mention-me' : ''}`}
          >
            {part}
          </span>
        );
      }
      return part;
    });
  };

  return (
    <div className="chat-window">
      {/* Chat header */}
      <div className="chat-header">
        <div className="chat-header-main-group">
          {/* Avatar for Direct or Group Chat */}
          <div
            className={`chat-header-avatar-btn ${isGroup ? 'group-avatar-btn' : ''}`}
            onClick={() => {
              if (isGroup) {
                setIsGroupModalOpen(true);
              } else if (otherUser && onOpenUserProfile) {
                onOpenUserProfile(otherUser);
              }
            }}
            title={isGroup ? 'Click for Group Details' : 'Click to view profile & DP'}
          >
            <div className="chat-header-avatar">
              {isGroup ? (
                '👥'
              ) : otherAvatarUrl ? (
                <>
                  <img
                    src={otherAvatarUrl}
                    alt={getConversationName()}
                    onError={(e) => {
                      e.currentTarget.style.display = 'none';
                      const fallback = e.currentTarget.nextElementSibling;
                      if (fallback) fallback.style.display = 'flex';
                    }}
                  />
                  <div className="chat-header-avatar-placeholder" style={{ display: 'none' }}>
                    {otherInitials}
                  </div>
                </>
              ) : (
                <div className="chat-header-avatar-placeholder">{otherInitials}</div>
              )}
            </div>
            {!isGroup && getLastSeenStatus() && (
              <span
                className={`chat-header-online-dot ${
                  getLastSeenStatus().isOnline ? 'online' : 'offline'
                }`}
              />
            )}
          </div>

          <div
            className={`chat-header-info-group ${isGroup ? 'clickable' : ''}`}
            onClick={() => isGroup && setIsGroupModalOpen(true)}
            title={isGroup ? 'Click for Group Details & Settings' : ''}
          >
            <div className="chat-header-title">
              <h2>{getConversationName()}</h2>
              {isGroup && (
                <span className="group-members-badge">
                  {participants.length} members • Settings ⚙️
                </span>
              )}
              {!isGroup && getLastSeenStatus() && (
                <span
                  className={`last-seen-header ${
                    getLastSeenStatus().isOnline ? 'online' : ''
                  }`}
                >
                  {getLastSeenStatus().isOnline && <span className="online-dot"></span>}
                  {getLastSeenStatus().text}
                </span>
              )}
            </div>
            {typingUsers.length > 0 && (
              <div className="typing-indicator">
                <span className="typing-text">{typingUsers.join(', ')} typing</span>
                <div className="typing-dots">
                  <span></span>
                  <span></span>
                  <span></span>
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Messages area */}
      <div className="messages-area">
        {loading && messages.length === 0 ? (
          <div className="chat-loading">
            <p>Loading messages...</p>
          </div>
        ) : messages.length === 0 ? (
          <div className="no-messages">
            <span className="empty-chat-icon">💬</span>
            <p>No messages yet. Say hello to get started!</p>
          </div>
        ) : (
          messages.map((message) => {
            const isMine = message.senderId === user?.id;
            const isDeleted = message.isDeleted;
            const isEditing = editingMessageId === message.id;
            const fullUrl = getFullAttachmentUrl(message.attachmentUrl);
            const isImage =
              message.type === 1 ||
              (message.attachmentUrl && message.attachmentUrl.match(/\.(jpeg|jpg|gif|png|webp|svg)$/i));
            const isFile = message.type === 2 || (message.attachmentUrl && !isImage);
            const canDelete = isMine || isCurrentUserAdmin;

            return (
              <div
                key={message.id}
                id={`msg-${message.id}`}
                className={`message-row ${isMine ? 'message-sent-row' : 'message-received-row'}`}
              >
                <div
                  className={`message ${isMine ? 'message-sent' : 'message-received'} ${
                    isDeleted ? 'message-deleted' : ''
                  }`}
                >
                  {/* Pure Quick Reaction Emoji Bar on Hover */}
                  {!isDeleted && !isEditing && (
                    <div className="reaction-action-bar">
                      {QUICK_REACTIONS.map((emoji) => (
                        <button
                          key={emoji}
                          type="button"
                          className="quick-reaction-btn"
                          onClick={() => handleToggleReaction(message.id, emoji)}
                          title={`React with ${emoji}`}
                        >
                          {emoji}
                        </button>
                      ))}
                    </div>
                  )}

                  {/* Message Corner Dropdown Button & Menu on Hover */}
                  {!isDeleted && !isEditing && (
                    <div className="msg-dropdown-container">
                      <button
                        type="button"
                        className={`msg-dropdown-trigger ${activeDropdownMsgId === message.id ? 'active' : ''}`}
                        onClick={(e) => {
                          e.stopPropagation();
                          setActiveDropdownMsgId(activeDropdownMsgId === message.id ? null : message.id);
                        }}
                        title="Message options"
                      >
                        ⋮
                      </button>

                      {activeDropdownMsgId === message.id && (
                        <div className="message-dropdown-menu" onClick={(e) => e.stopPropagation()}>
                          {/* 1. Reply Option */}
                          <button
                            type="button"
                            className="msg-dropdown-item"
                            onClick={() => {
                              setReplyingTo(message);
                              setActiveDropdownMsgId(null);
                              messageInputRef.current?.focus();
                            }}
                          >
                            <span className="dropdown-item-icon">↩️</span>
                            <span>Reply</span>
                          </button>

                          {/* 2. Copy Text Option */}
                          {message.content && !isImage && !isFile && (
                            <button
                              type="button"
                              className="msg-dropdown-item"
                              onClick={() => {
                                navigator.clipboard?.writeText(message.content);
                                setActiveDropdownMsgId(null);
                              }}
                            >
                              <span className="dropdown-item-icon">📋</span>
                              <span>Copy Text</span>
                            </button>
                          )}

                          {/* 3. Edit Option (Only sender and text messages) */}
                          {isMine && !isImage && !isFile && (
                            <button
                              type="button"
                              className="msg-dropdown-item"
                              onClick={() => {
                                handleStartEdit(message);
                                setActiveDropdownMsgId(null);
                              }}
                            >
                              <span className="dropdown-item-icon">✏️</span>
                              <span>Edit Message</span>
                            </button>
                          )}

                          {/* 4. Delete Option (Sender or Group Admin) */}
                          {canDelete && (
                            <button
                              type="button"
                              className="msg-dropdown-item danger"
                              onClick={() => {
                                handleDeleteMessage(message.id);
                                setActiveDropdownMsgId(null);
                              }}
                            >
                              <span className="dropdown-item-icon">🗑️</span>
                              <span>Delete for Everyone</span>
                            </button>
                          )}
                        </div>
                      )}
                    </div>
                  )}

                  <div className="message-content">
                    {/* Sender name for group chats or received messages */}
                    {!isMine && (
                      <span className="message-sender">
                        {message.senderName}
                        {isGroup &&
                          participants.find((p) => p.id === message.senderId)?.isAdmin && (
                            <span className="sender-admin-badge">👑</span>
                          )}
                      </span>
                    )}

                    {/* Quoted Message Preview Header */}
                    {message.replyToMessageId && (
                      <div
                        className="quoted-message-bubble"
                        onClick={() => scrollToMessage(message.replyToMessageId)}
                        title="Click to jump to original message"
                      >
                        <div className="quoted-sender">
                          ↩️ {message.replyToSenderName || 'Quoted Message'}
                        </div>
                        <div className="quoted-snippet">
                          {message.replyToContent || 'Attachment'}
                        </div>
                      </div>
                    )}

                    {/* Deleted Message Placeholder */}
                    {isDeleted ? (
                      <p className="deleted-message-text">
                        <span className="deleted-icon">🚫</span> This message was deleted
                      </p>
                    ) : isEditing ? (
                      /* Inline Message Edit Mode */
                      <div className="inline-edit-box">
                        <input
                          type="text"
                          className="inline-edit-input"
                          value={editContent}
                          onChange={(e) => setEditContent(e.target.value)}
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') handleSaveEdit(message.id);
                            if (e.key === 'Escape') handleCancelEdit();
                          }}
                          autoFocus
                        />
                        <div className="inline-edit-actions">
                          <button
                            type="button"
                            className="btn-edit-save"
                            onClick={() => handleSaveEdit(message.id)}
                          >
                            Save
                          </button>
                          <button
                            type="button"
                            className="btn-edit-cancel"
                            onClick={handleCancelEdit}
                          >
                            Cancel
                          </button>
                        </div>
                      </div>
                    ) : (
                      <>
                        {/* Image Attachment */}
                        {isImage && message.attachmentUrl && (
                          <div
                            className="message-image-container"
                            onClick={() => setLightboxImage(fullUrl)}
                          >
                            <img
                              src={fullUrl}
                              alt="Attachment"
                              className="message-image-preview"
                            />
                          </div>
                        )}

                        {/* File Attachment */}
                        {isFile && message.attachmentUrl && (
                          <a
                            href={fullUrl}
                            target="_blank"
                            rel="noreferrer"
                            className="message-file-card"
                            download
                          >
                            <span className="file-card-icon">📄</span>
                            <div className="file-card-details">
                              <span className="file-card-name">
                                {message.fileName || 'Download File'}
                              </span>
                              <span className="file-card-action">Click to download</span>
                            </div>
                          </a>
                        )}

                        {/* Message Text */}
                        {message.content &&
                          (!isImage || message.content !== 'Sent an image') &&
                          (!isFile || message.content !== message.fileName) && (
                            <p className="message-text">
                              {renderMessageContent(message.content)}
                            </p>
                          )}
                      </>
                    )}

                    <div className="message-footer">
                      <span className="message-time">{formatMessageTime(message.sentAt)}</span>
                      {message.isEdited && !isDeleted && (
                        <span className="message-edited-tag">(edited)</span>
                      )}
                      {isMine && !isDeleted && (
                        <span className="message-status-ticks">
                          {message.isRead ? '✓✓' : '✓'}
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Reaction Badges */}
                  {!isDeleted && message.reactions && message.reactions.length > 0 && (
                    <div className="message-reactions-container">
                      {message.reactions.map((reaction) => (
                        <button
                          key={reaction.emoji}
                          type="button"
                          className={`reaction-badge ${reaction.hasReacted ? 'active' : ''}`}
                          onClick={() => handleToggleReaction(message.id, reaction.emoji)}
                          title={reaction.usernames?.join(', ') || 'Reactions'}
                        >
                          <span className="reaction-emoji">{reaction.emoji}</span>
                          {/* Show counter only in group chats or if count > 1 */}
                          {isGroup && reaction.count > 1 ? (
                            <span className="reaction-count">{reaction.count}</span>
                          ) : null}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              </div>
            );
          })
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Feature 4: Quoted Reply Banner Docked Above Input */}
      {replyingTo && (
        <div className="reply-preview-dock">
          <div className="reply-preview-border" />
          <div className="reply-preview-content">
            <span className="reply-preview-title">
              Replying to <strong className="reply-author">{replyingTo.senderName}</strong>
            </span>
            <span className="reply-preview-snippet">
              {replyingTo.isDeleted
                ? 'Deleted message'
                : replyingTo.content || (replyingTo.type === 1 ? '📷 Photo' : '📄 File')}
            </span>
          </div>
          <button
            type="button"
            className="reply-close-btn"
            onClick={() => setReplyingTo(null)}
            title="Cancel Reply"
          >
            ✕
          </button>
        </div>
      )}

      {/* Feature 5: @Mentions Autocomplete Popover */}
      {mentionCandidates.length > 0 && (
        <div className="mentions-popover">
          <div className="mentions-popover-header">Mention Member</div>
          {mentionCandidates.map((participant) => (
            <div
              key={participant.id}
              className="mention-item"
              onClick={() => handleSelectMention(participant)}
            >
              <div className="mention-avatar">
                {participant.displayName?.[0]?.toUpperCase() || 'U'}
              </div>
              <div className="mention-info">
                <span className="mention-name">{participant.displayName || participant.username}</span>
                <span className="mention-username">@{participant.username}</span>
              </div>
              {participant.isAdmin && <span className="mention-admin-crown">👑</span>}
            </div>
          ))}
        </div>
      )}

      {/* Selected File / Attachment Preview Bar */}
      {filePreview && (
        <div className="attachment-preview-bar">
          <div className="preview-info">
            {filePreview.type === 'image' ? (
              <img src={filePreview.url} alt="Preview" className="preview-thumb" />
            ) : (
              <span className="preview-file-icon">📎</span>
            )}
            <div className="preview-text">
              <span className="preview-filename">{filePreview.name}</span>
              <span className="preview-filesize">{filePreview.size}</span>
            </div>
          </div>
          <button type="button" className="remove-preview-btn" onClick={removeSelectedFile}>
            ✕
          </button>
        </div>
      )}

      {/* Message input */}
      <form onSubmit={handleSendMessage} className="message-input-form">
        {/* Hidden File Input */}
        <input
          type="file"
          ref={fileInputRef}
          onChange={handleFileSelect}
          style={{ display: 'none' }}
          accept="image/*,.pdf,.doc,.docx,.txt,.zip,.rar"
        />

        {/* Attachment Button */}
        <button
          type="button"
          className="input-addon-btn"
          onClick={() => fileInputRef.current?.click()}
          title="Attach Image or File"
          disabled={sending || uploading}
        >
          📎
        </button>

        {/* Emoji Toggle Button */}
        <div className="emoji-picker-container">
          <button
            type="button"
            className={`input-addon-btn emoji-trigger ${showEmojiPicker ? 'active' : ''}`}
            onClick={() => setShowEmojiPicker(!showEmojiPicker)}
            title="Insert Emoji"
          >
            😀
          </button>

          {showEmojiPicker && (
            <EmojiPicker
              onSelectEmoji={handleInsertEmoji}
              onClose={() => setShowEmojiPicker(false)}
            />
          )}
        </div>

        {/* Main Text Input */}
        <input
          ref={messageInputRef}
          type="text"
          value={newMessage}
          onChange={handleInputChange}
          onBlur={handleTypingEnd}
          placeholder={
            uploading
              ? 'Uploading attachment...'
              : replyingTo
              ? `Reply to ${replyingTo.senderName}...`
              : isGroup
              ? 'Type a message... (Type @ to mention)'
              : 'Type a message...'
          }
          className="message-input"
          disabled={uploading}
        />

        {/* Send Button */}
        <button
          type="submit"
          className="btn-send"
          onMouseDown={(e) => e.preventDefault()}
          disabled={sending || uploading || (!newMessage.trim() && !selectedFile)}
        >
          {uploading ? 'Uploading...' : sending ? 'Sending...' : 'Send'}
        </button>
      </form>

      {/* Group Details & Member Management Modal */}
      {isGroup && (
        <GroupDetailsModal
          isOpen={isGroupModalOpen}
          onClose={() => setIsGroupModalOpen(false)}
          conversation={conversation}
          currentUser={user}
          onGroupUpdated={(updated) => {
            if (onGroupUpdated) onGroupUpdated(updated);
          }}
          onLeaveGroup={(convId) => {
            if (onLeaveGroup) onLeaveGroup(convId);
          }}
        />
      )}

      {/* Fullscreen Image Lightbox Modal */}
      {lightboxImage && (
        <div className="image-lightbox-modal" onClick={() => setLightboxImage(null)}>
          <div className="lightbox-content" onClick={(e) => e.stopPropagation()}>
            <button
              type="button"
              className="btn-lightbox-close-top"
              onClick={() => setLightboxImage(null)}
              title="Close (Esc)"
              aria-label="Close image"
            >
              ✕
            </button>
            <img src={lightboxImage} alt="Fullscreen View" className="lightbox-img" />
            <div className="lightbox-actions">
              <a
                href={lightboxImage}
                download="attachment"
                target="_blank"
                rel="noreferrer"
                className="lightbox-btn-download"
              >
                ⬇ Download Full Size
              </a>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
