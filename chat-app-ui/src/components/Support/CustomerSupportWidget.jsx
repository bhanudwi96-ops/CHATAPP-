// ─────────────────────────────────────────────────────────────────────────────
// Phase 3 — Streaming toggle.
// Set to false to fall back to the original non-streaming path instantly.
// ─────────────────────────────────────────────────────────────────────────────
const USE_STREAMING = true;

import React, { useState, useEffect, useRef, useCallback } from 'react';
import { useLocation } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import MarkdownRenderer from './MarkdownRenderer';
import signalRService from '../../services/signalr';
import './CustomerSupportWidget.css';

// ── Memoized message bubble to prevent re-rendering completed history during stream ──
const MessageRow = React.memo(function MessageRow({ msg }) {
  return (
    <div
      className={`support-msg-row ${msg.role === 'user' ? 'msg-user-row' : 'msg-bot-row'}`}
    >
      {msg.role === 'bot' && (
        <div className="msg-bot-avatar">🤖</div>
      )}

      <div className="msg-bubble-wrap">
        <div className={`msg-bubble ${msg.role === 'user' ? 'bubble-user' : 'bubble-bot'} ${msg.isError ? 'bubble-error' : ''}`}>
          <div className="msg-text-content">
            {msg.role === 'bot' ? (
              <>
                <MarkdownRenderer content={msg.content} />
                {msg.isStreaming && <span className="streaming-cursor" aria-hidden="true">▌</span>}
              </>
            ) : (
              msg.content.split('\n').map((line, i) => (
                <p key={i} className="msg-paragraph">{line}</p>
              ))
            )}
          </div>

          {msg.ticketCreated && msg.ticketId && (
            <div className="ticket-success-card">
              <div className="ticket-card-header">
                <span className="ticket-card-icon">🎫</span>
                <span className="ticket-card-title">Support Ticket Dispatched</span>
              </div>
              <div className="ticket-card-id">Reference: #{msg.ticketId}</div>
              <div className="ticket-card-note">
                A confirmation email was sent to your registered address. Our human support engineers have been alerted!
              </div>
            </div>
          )}

          <span className="msg-timestamp">
            {new Date(msg.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
          </span>
        </div>
      </div>
    </div>
  );
});

export default function CustomerSupportWidget() {
  const location = useLocation();
  const isChatRoute = location?.pathname?.startsWith('/chat');

  const { user, isAuthenticated } = useAuth();
  const [isOpen, setIsOpen] = useState(false);
  const [activeTab, setActiveTab] = useState('chat'); // 'chat' | 'tickets'
  const [sessionId, setSessionId] = useState(null);
  const [messages, setMessages] = useState([]);
  const [inputMessage, setInputMessage] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [guestName, setGuestName] = useState('');
  const [hasUnread, setHasUnread] = useState(false);
  const [myTickets, setMyTickets] = useState([]);
  const [ticketsLoading, setTicketsLoading] = useState(false);
  const [ticketsError, setTicketsError] = useState('');
  const [resolutionNotification, setResolutionNotification] = useState(null);

  // Global listeners for Top Navbar AI Copilot button
  useEffect(() => {
    const handleOpen = () => setIsOpen(true);
    const handleToggle = () => setIsOpen(prev => !prev);
    const handleClose = () => setIsOpen(false);

    window.addEventListener('open-support-ai', handleOpen);
    window.addEventListener('toggle-support-ai', handleToggle);
    window.addEventListener('close-support-ai', handleClose);

    return () => {
      window.removeEventListener('open-support-ai', handleOpen);
      window.removeEventListener('toggle-support-ai', handleToggle);
      window.removeEventListener('close-support-ai', handleClose);
    };
  }, []);

  const messagesEndRef = useRef(null);
  const inputRef = useRef(null);
  const chatContainerRef = useRef(null);
  const isNearBottomRef = useRef(true);

  const handleMessagesScroll = useCallback(() => {
    if (!chatContainerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = chatContainerRef.current;
    isNearBottomRef.current = scrollHeight - scrollTop - clientHeight <= 80;
  }, []);

  // Active username & email resolution
  const activeDisplayName = isAuthenticated
    ? (user?.displayName || user?.username || 'Valued User')
    : (guestName || null);

  const activeAccountUsername = isAuthenticated
    ? (user?.username || null)
    : (guestName || null);

  const activeUsername = activeDisplayName;

  const activeEmail = isAuthenticated ? (user?.email || null) : null;

  // Track previous user identifier to detect login/logout/user switch transitions
  const currentUserKey = isAuthenticated && user ? (user.id || user.username || 'auth-user') : null;
  const prevUserKeyRef = useRef(currentUserKey);
  const isInitialMountRef = useRef(true);

  // Initialize and synchronize AI Support state with auth state changes
  useEffect(() => {
    if (isInitialMountRef.current) {
      isInitialMountRef.current = false;
      // On fresh load / reload, wipe out stale guest session identifiers to prevent ghost memory
      sessionStorage.removeItem('support_ai_session_id');
      sessionStorage.removeItem('support_ai_guest_name');
      setSessionId(null);
      setGuestName('');

      if (isAuthenticated && user) {
        setMessages([
          {
            id: `init-${Date.now()}`,
            role: 'bot',
            content: `Hello ${user.displayName || user.username}! 👋 I'm your ChatApp AI Copilot. How can I assist you today?`,
            timestamp: new Date()
          }
        ]);
      } else {
        setMessages([
          {
            id: `init-${Date.now()}`,
            role: 'bot',
            content: `Hello! 👋 Welcome to ChatApp AI Copilot. Before we begin, could you please tell me your **name or username** so I know who I'm chatting with?`,
            timestamp: new Date()
          }
        ]);
      }
      return;
    }

    const prevUserKey = prevUserKeyRef.current;
    if (prevUserKey !== currentUserKey) {
      prevUserKeyRef.current = currentUserKey;

      if (!currentUserKey) {
        // --- LOGOUT TRANSITION ---
        // 1. Immediately close the chat widget
        setIsOpen(false);
        // 2. Clear previous user's conversation and session
        setSessionId(null);
        setGuestName('');
        setInputMessage('');
        setError('');
        setHasUnread(false);
        sessionStorage.removeItem('support_ai_session_id');
        sessionStorage.removeItem('support_ai_guest_name');
        // 3. Reset greeting to guest mode for next visitor
        setMessages([
          {
            id: `init-guest-${Date.now()}`,
            role: 'bot',
            content: `Hello! 👋 Welcome to ChatApp AI Copilot. Before we begin, could you please tell me your **name or username** so I know who I'm chatting with?`,
            timestamp: new Date()
          }
        ]);
      } else {
        // --- LOGIN TRANSITION (or user switch) ---
        // 1. Clear any prior guest session or other account's session
        setSessionId(null);
        setGuestName('');
        setInputMessage('');
        setError('');
        sessionStorage.removeItem('support_ai_session_id');
        sessionStorage.removeItem('support_ai_guest_name');
        // 2. Immediately recognize user by name in greeting
        const displayName = user?.displayName || user?.username || 'Valued User';
        setMessages([
          {
            id: `init-auth-${Date.now()}`,
            role: 'bot',
            content: `Hello ${displayName}! 👋 I'm your ChatApp AI Copilot. How can I assist you today?`,
            timestamp: new Date()
          }
        ]);
      }
    }
  }, [isAuthenticated, user, currentUserKey]);

  // Scroll to bottom only when a new message bubble is added or widget is opened
  useEffect(() => {
    if (isOpen) {
      messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [messages.length, isOpen]);

  // Fetch ticket history for logged-in user
  const fetchMyTickets = useCallback(async () => {
    if (!isAuthenticated) return;
    setTicketsLoading(true);
    setTicketsError('');
    try {
      const token = localStorage.getItem('token') || sessionStorage.getItem('token');
      const res = await fetch('/api/support/my-tickets', {
        headers: token ? { Authorization: `Bearer ${token}` } : {}
      });
      if (!res.ok) throw new Error(`Failed to load tickets (${res.status})`);
      const data = await res.json();
      setMyTickets(data);
    } catch (err) {
      setTicketsError(err.message || 'Could not load tickets.');
    } finally {
      setTicketsLoading(false);
    }
  }, [isAuthenticated]);

  // Load tickets on mount or when switching to the Tickets tab
  useEffect(() => {
    if (isAuthenticated) {
      fetchMyTickets();
    }
  }, [fetchMyTickets, isAuthenticated, activeTab]);

  // Subscribe to SignalR ticketResolved events for real-time resolution notifications
  useEffect(() => {
    const handler = ({ referenceNumber, resolutionSummary }) => {
      setResolutionNotification({ referenceNumber, resolutionSummary });
      setHasUnread(true);
      // Refresh ticket list if it's cached
      setMyTickets(prev =>
        prev.map(t =>
          t.referenceNumber === referenceNumber
            ? { ...t, status: 'Resolved' }
            : t
        )
      );
    };
    signalRService.onTicketResolved(handler);
    return () => signalRService.removeHandler('ticketResolved', handler);
  }, []);

  // Focus input when modal opens
  useEffect(() => {
    if (isOpen) {
      setHasUnread(false);
      setTimeout(() => {
        inputRef.current?.focus();
      }, 150);
    }
  }, [isOpen]);

  // Handle resetting conversation
  const handleResetSession = async () => {
    sessionStorage.removeItem('support_ai_session_id');
    sessionStorage.removeItem('support_ai_guest_name');
    setSessionId(null);
    setGuestName('');
    setError('');

    if (isAuthenticated && user) {
      setMessages([
        {
          id: `init-${Date.now()}`,
          role: 'bot',
          content: `Hello again, ${user.displayName || user.username}! 👋 How can I help you?`,
          timestamp: new Date()
        }
      ]);
    } else {
      setMessages([
        {
          id: `init-${Date.now()}`,
          role: 'bot',
          content: `Hello! 👋 Welcome to ChatApp Support. Before we begin, please tell me your **name or username**.`,
          timestamp: new Date()
        }
      ]);
    }
  };

  // Ensure session exists
  const ensureSession = async () => {
    if (sessionId) return sessionId;

    try {
      const res = await fetch('/api/sessions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          username: activeAccountUsername || activeUsername,
          displayName: activeDisplayName,
          email: activeEmail
        })
      });

      if (!res.ok) {
        throw new Error('Failed to initialize support session');
      }

      const data = await res.json();
      setSessionId(data.sessionId);
      sessionStorage.setItem('support_ai_session_id', data.sessionId);
      return data.sessionId;
    } catch (err) {
      console.error('Error creating session:', err);
      return null;
    }
  };

  // Send message
  const handleSendMessage = async (textToSend) => {
    const query = (textToSend || inputMessage).trim();
    if (!query || loading) return;

    // Check if guest user might be introducing themselves
    if (!isAuthenticated && !guestName) {
      const nameMatch = query.match(/(?:my name is|i am|i'm|call me|this is)\s+([A-Za-z0-9_.-]+)/i);
      if (nameMatch && nameMatch[1]) {
        const extracted = nameMatch[1];
        setGuestName(extracted);
        sessionStorage.setItem('support_ai_guest_name', extracted);
      } else if (query.split(' ').length <= 2 && query.length < 25 && !query.includes('?')) {
        // User just replied with their name
        setGuestName(query);
        sessionStorage.setItem('support_ai_guest_name', query);
      }
    }

    const userMsg = {
      id: `user-${Date.now()}`,
      role: 'user',
      content: query,
      timestamp: new Date()
    };

    setMessages((prev) => [...prev, userMsg]);
    setInputMessage('');
    setLoading(true);
    setError('');

    try {
      const currentSessionId = await ensureSession();
      if (!currentSessionId) {
        throw new Error('Could not establish chat connection.');
      }

      const token = localStorage.getItem('token') || sessionStorage.getItem('token');
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: JSON.stringify({
          sessionId: currentSessionId,
          message: query,
          username: activeAccountUsername || activeUsername,
          displayName: activeDisplayName,
          email: activeEmail
        })
      });

      if (!res.ok) {
        const errorData = await res.json().catch(() => ({}));
        throw new Error(errorData.error || errorData.title || `Server error (${res.status})`);
      }

      const data = await res.json();

      const botMsg = {
        id: `bot-${Date.now()}`,
        role: 'bot',
        content: data.reply || data.response || 'I processed your request.',
        timestamp: new Date(),
        ticketCreated: data.ticketCreated,
        ticketId: data.ticketId
      };

      setMessages((prev) => [...prev, botMsg]);

      if (data.ticketCreated) {
        fetchMyTickets();
      }

      if (!isOpen) {
        setHasUnread(true);
      }
    } catch (err) {
      console.error('Chat error:', err);
      setError(err.message || 'Something went wrong. Please try again.');
      setMessages((prev) => [
        ...prev,
        {
          id: `bot-err-${Date.now()}`,
          role: 'bot',
          content: `⚠️ Error: ${err.message || 'Unable to get response from support assistant.'}`,
          timestamp: new Date(),
          isError: true
        }
      ]);
    } finally {
      setLoading(false);
      setTimeout(() => {
        inputRef.current?.focus();
      }, 50);
    }
  };

  // ─────────────────────────────────────────────────────────────────────────
  // Phase 3 — Streaming send handler (ReadableStream / fetch SSE)
  // ─────────────────────────────────────────────────────────────────────────
  const handleSendMessageStreaming = async (textToSend) => {
    const query = (textToSend || inputMessage).trim();
    if (!query || loading) return;

    // Guest name detection (same as non-streaming path)
    if (!isAuthenticated && !guestName) {
      const nameMatch = query.match(/(?:my name is|i am|i'm|call me|this is)\s+([A-Za-z0-9_.-]+)/i);
      if (nameMatch?.[1]) {
        setGuestName(nameMatch[1]);
        sessionStorage.setItem('support_ai_guest_name', nameMatch[1]);
      } else if (query.split(' ').length <= 2 && query.length < 25 && !query.includes('?')) {
        setGuestName(query);
        sessionStorage.setItem('support_ai_guest_name', query);
      }
    }

    // Show the user's message immediately
    const userMsg = {
      id: `user-${Date.now()}`,
      role: 'user',
      content: query,
      timestamp: new Date()
    };
    setMessages(prev => [...prev, userMsg]);
    setInputMessage('');
    setLoading(true);
    setError('');

    // Seed an empty bot placeholder — this shows the typing cursor right away
    const botId = `bot-stream-${Date.now()}`;
    const botPlaceholder = {
      id: botId,
      role: 'bot',
      content: '',
      timestamp: new Date(),
      isStreaming: true,
      ticketCreated: false,
      ticketId: null
    };
    setMessages(prev => [...prev, botPlaceholder]);

    try {
      const currentSessionId = await ensureSession();
      if (!currentSessionId) throw new Error('Could not establish chat connection.');

      const token = localStorage.getItem('token') || sessionStorage.getItem('token');
      const res = await fetch('/api/chat/stream', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: JSON.stringify({
          sessionId: currentSessionId,
          message: query,
          username: activeAccountUsername || activeUsername,
          displayName: activeDisplayName,
          email: activeEmail
        })
      });

      if (!res.ok || !res.body) {
        const errData = await res.json().catch(() => ({}));
        throw new Error(errData.error || errData.title || `Server error (${res.status})`);
      }

      // ── ReadableStream reader with frame-throttled batching ───────────────
      const reader = res.body.getReader();
      const decoder = new TextDecoder('utf-8');
      let buffer = '';
      let pendingTokens = '';
      let rafId = null;
      let lastFlushTime = 0;
      const FRAME_MS = 40; // ~25 FPS batching window

      const flushTokens = () => {
        if (!pendingTokens) return;
        const textToAppend = pendingTokens;
        pendingTokens = '';
        lastFlushTime = performance.now();

        setMessages(prev => prev.map(m =>
          m.id === botId
            ? { ...m, content: m.content + textToAppend }
            : m
        ));

        if (isNearBottomRef.current && chatContainerRef.current) {
          chatContainerRef.current.scrollTop = chatContainerRef.current.scrollHeight;
        }
      };

      const scheduleFlush = () => {
        if (rafId) return;
        const now = performance.now();
        const elapsed = now - lastFlushTime;

        if (elapsed >= FRAME_MS) {
          rafId = requestAnimationFrame(() => {
            rafId = null;
            flushTokens();
          });
        } else {
          rafId = setTimeout(() => {
            requestAnimationFrame(() => {
              rafId = null;
              flushTokens();
            });
          }, FRAME_MS - elapsed);
        }
      };

      const cancelScheduledFlush = () => {
        if (rafId) {
          cancelAnimationFrame(rafId);
          clearTimeout(rafId);
          rafId = null;
        }
      };

      const processChunks = async () => {
        while (true) {
          const { value, done } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });

          // SSE may arrive in multiple partial TCP segments — split on double-newline
          const parts = buffer.split('\n\n');
          // Last part may be incomplete — keep it in buffer
          buffer = parts.pop() || '';

          for (const part of parts) {
            const dataLine = part.split('\n').find(l => l.startsWith('data: '));
            if (!dataLine) continue;

            const jsonStr = dataLine.slice(6).trim(); // strip 'data: '
            let parsed;
            try { parsed = JSON.parse(jsonStr); } catch { continue; }

            if (parsed.event === 'done') {
              cancelScheduledFlush();
              flushTokens(); // Synchronous final flush of any remaining buffered tokens
              setMessages(prev => prev.map(m =>
                m.id === botId
                  ? { ...m, isStreaming: false, ticketCreated: parsed.ticketCreated, ticketId: parsed.ticketId }
                  : m
              ));
              if (parsed.ticketCreated) fetchMyTickets();
              if (!isOpen) setHasUnread(true);
              return; // done
            }

            if (parsed.event === 'error') {
              cancelScheduledFlush();
              flushTokens();
              setMessages(prev => prev.map(m =>
                m.id === botId
                  ? { ...m, isStreaming: false, content: m.content || `⚠️ ${parsed.message || 'Error from AI.'}`, isError: true }
                  : m
              ));
              return;
            }

            if (parsed.chunk) {
              // Append to in-memory pending token buffer and schedule frame flush
              pendingTokens += parsed.chunk;
              scheduleFlush();
            }
          }
        }

        // Stream ended without a 'done' event — final flush and mark complete
        cancelScheduledFlush();
        flushTokens();
        setMessages(prev => prev.map(m =>
          m.id === botId ? { ...m, isStreaming: false } : m
        ));
      };

      await processChunks();

    } catch (err) {
      console.error('[Stream] Chat error:', err);
      setError(err.message || 'Something went wrong. Please try again.');
      // Replace the streaming placeholder with the error
      setMessages(prev => prev.map(m =>
        m.id === botId
          ? { ...m, isStreaming: false, content: `⚠️ ${err.message || 'Unable to get response.'}`, isError: true }
          : m
      ));
    } finally {
      setLoading(false);
      setTimeout(() => { inputRef.current?.focus(); }, 50);
    }
  };

  // Active send dispatcher — routes to streaming or legacy based on flag
  const handleSend = USE_STREAMING ? handleSendMessageStreaming : handleSendMessage;

  // --- Floating / Draggable state ---

  const [btnPos, setBtnPos] = useState(() => {
    try {
      const saved = sessionStorage.getItem('support_ai_btn_pos');
      return saved ? JSON.parse(saved) : null;
    } catch {
      return null;
    }
  });

  const [panelPos, setPanelPos] = useState(() => {
    try {
      const saved = sessionStorage.getItem('support_ai_panel_pos');
      return saved ? JSON.parse(saved) : null;
    } catch {
      return null;
    }
  });

  const [isDraggingBtn, setIsDraggingBtn] = useState(false);
  const [isDraggingPanel, setIsDraggingPanel] = useState(false);

  const btnRef = useRef(null);
  const panelRef = useRef(null);

  // Keep positions clamped when window is resized
  useEffect(() => {
    const handleResize = () => {
      setBtnPos((prev) => {
        if (!prev) return prev;
        const maxX = Math.max(12, window.innerWidth - 170);
        const maxY = Math.max(12, window.innerHeight - 60);
        return {
          x: Math.min(Math.max(12, prev.x), maxX),
          y: Math.min(Math.max(12, prev.y), maxY)
        };
      });
      setPanelPos((prev) => {
        if (!prev) return prev;
        const maxX = Math.max(10, window.innerWidth - 410);
        const maxY = Math.max(10, window.innerHeight - 590);
        return {
          x: Math.min(Math.max(10, prev.x), maxX),
          y: Math.min(Math.max(10, prev.y), maxY)
        };
      });
    };
    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, []);

  // Drag handler for Floating Trigger Button
  const handleBtnPointerDown = (e) => {
    if (e.button !== 0) return;
    const btn = btnRef.current;
    if (!btn) return;

    const rect = btn.getBoundingClientRect();
    const shiftX = e.clientX - rect.left;
    const shiftY = e.clientY - rect.top;
    let didDrag = false;
    const startX = e.clientX;
    const startY = e.clientY;

    const onPointerMove = (moveEvent) => {
      const dx = moveEvent.clientX - startX;
      const dy = moveEvent.clientY - startY;
      if (!didDrag && Math.hypot(dx, dy) > 4) {
        didDrag = true;
        setIsDraggingBtn(true);
      }
      if (didDrag) {
        const maxX = Math.max(12, window.innerWidth - rect.width - 12);
        const maxY = Math.max(12, window.innerHeight - rect.height - 12);
        const newX = Math.min(Math.max(12, moveEvent.clientX - shiftX), maxX);
        const newY = Math.min(Math.max(12, moveEvent.clientY - shiftY), maxY);
        const newPos = { x: Math.round(newX), y: Math.round(newY) };
        setBtnPos(newPos);
        sessionStorage.setItem('support_ai_btn_pos', JSON.stringify(newPos));
      }
    };

    const onPointerUp = () => {
      setIsDraggingBtn(false);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', onPointerUp);
      if (!didDrag) {
        // Clean click -> open chat
        setIsOpen(true);
      }
    };

    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
  };

  // Drag handler for Chat Panel Header
  const handlePanelPointerDown = (e) => {
    if (e.target.closest('button')) return;
    if (e.button !== 0) return;
    const panel = panelRef.current;
    if (!panel) return;

    const rect = panel.getBoundingClientRect();
    const shiftX = e.clientX - rect.left;
    const shiftY = e.clientY - rect.top;

    setIsDraggingPanel(true);

    const onPointerMove = (moveEvent) => {
      const maxX = Math.max(10, window.innerWidth - rect.width - 10);
      const maxY = Math.max(10, window.innerHeight - rect.height - 10);
      const newX = Math.min(Math.max(10, moveEvent.clientX - shiftX), maxX);
      const newY = Math.min(Math.max(10, moveEvent.clientY - shiftY), maxY);
      const newPos = { x: Math.round(newX), y: Math.round(newY) };
      setPanelPos(newPos);
      sessionStorage.setItem('support_ai_panel_pos', JSON.stringify(newPos));
    };

    const onPointerUp = () => {
      setIsDraggingPanel(false);
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerup', onPointerUp);
    };

    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
  };

  const handleKeyDown = (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const quickPrompts = [
    'How do I create a group chat?',
    'I forgot my account password',
    'How to change theme?',
    'How to edit or delete a message?',
    'How does live voice/video call work?',
    'File a support ticket for me'
  ];

  return (
    <div className="support-widget-container" aria-label="Customer Support Assistant">
      {/* Floating Trigger Button: only shown on non-chat public pages since chat has fixed top-header AI Copilot */}
      {!isOpen && !isChatRoute && (
        <div
          ref={btnRef}
          className={`support-floating-trigger ${isDraggingBtn ? 'is-dragging' : ''}`}
          style={btnPos ? { left: `${btnPos.x}px`, top: `${btnPos.y}px`, right: 'auto', bottom: 'auto' } : {}}
          onPointerDown={handleBtnPointerDown}
          role="button"
          tabIndex={0}
          aria-label="Open ChatApp AI Copilot (Drag to move)"
          title="Open AI Copilot (Drag anywhere to reposition)"
        >
          <div className="trigger-pulse-glow" />
          <div className="trigger-icon-box">
            <svg
              className="trigger-bot-icon"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="M12 8V4H8" />
              <rect width="16" height="12" x="4" y="8" rx="2" />
              <path d="M2 14h2" />
              <path d="M20 14h2" />
              <path d="M15 13v2" />
              <path d="M9 13v2" />
            </svg>
          </div>
          <span className="trigger-label">AI Copilot</span>
          <span className="trigger-drag-handle" title="Drag to move">⋮⋮</span>
          {hasUnread && <span className="trigger-unread-dot" />}
        </div>
      )}

      {/* Floating Chat Modal / Drawer (Draggable by Header) */}
      {isOpen && (
        <div
          ref={panelRef}
          className={`support-chat-panel animate-scale-in ${isDraggingPanel ? 'is-dragging' : ''}`}
          style={panelPos ? { left: `${panelPos.x}px`, top: `${panelPos.y}px`, right: 'auto', bottom: 'auto' } : {}}
          role="dialog"
          aria-modal="true"
        >
          {/* Tab Bar */}
          <div className="support-tab-bar">
            <button
              type="button"
              className={`support-tab-btn ${activeTab === 'chat' ? 'tab-active' : ''}`}
              onClick={() => setActiveTab('chat')}
            >
              💬 Chat
            </button>
            {isAuthenticated && (
              <button
                type="button"
                className={`support-tab-btn ${activeTab === 'tickets' ? 'tab-active' : ''}`}
                onClick={() => setActiveTab('tickets')}
              >
                🎫 My Tickets
                {myTickets.length > 0 && (
                  <span className="tab-badge" title={`${myTickets.length} total tickets`}>
                    {myTickets.filter(t => (t.status || '').toLowerCase() === 'open' || (t.status || '').toLowerCase() === 'inprogress').length || myTickets.length}
                  </span>
                )}
              </button>
            )}
          </div>

          {/* Resolution Notification Banner (SignalR push) */}
          {resolutionNotification && (
            <div className="support-resolution-banner">
              <span className="resolution-banner-icon">✅</span>
              <div className="resolution-banner-text">
                <div className="resolution-banner-title">Ticket {resolutionNotification.referenceNumber} Resolved!</div>
                <div className="resolution-banner-body">{resolutionNotification.resolutionSummary}</div>
              </div>
              <button
                type="button"
                className="resolution-banner-close"
                onClick={() => setResolutionNotification(null)}
                aria-label="Dismiss notification"
              >✕</button>
            </div>
          )}

          {/* Header (Draggable) */}
          <div
            className="support-chat-header"
            onPointerDown={handlePanelPointerDown}
            title="Drag header to move chat window"
          >
            <div className="header-left">
              <div className="header-avatar">
                <span className="avatar-bot-emoji">🤖</span>
                <span className="avatar-online-dot" />
              </div>
              <div className="header-info">
                <div className="header-title-row">
                  <span className="header-title">ChatApp AI Copilot</span>
                  <span className="header-badge">Online</span>
                </div>
                <div className="header-user-status">
                  {isAuthenticated ? (
                    <span>Recognized as <strong>{user?.displayName || user?.username}</strong></span>
                  ) : (
                    <span>{guestName ? `Recognized as ${guestName}` : 'Guest Mode (Identify yourself)'}</span>
                  )}
                </div>
              </div>
            </div>

            <div className="header-actions" onPointerDown={(e) => e.stopPropagation()}>
              <div className="header-drag-hint" title="Drag header to reposition">
                <span>⋮⋮ Move</span>
              </div>
              <button
                type="button"
                className="header-btn-action"
                onClick={handleResetSession}
                title="Restart conversation"
                aria-label="Restart conversation"
              >
                🔄
              </button>
              <button
                type="button"
                className="header-btn-action close-btn"
                onClick={() => setIsOpen(false)}
                title="Close chat"
                aria-label="Close chat"
              >
                ✕
              </button>
            </div>
          </div>

          {/* ── MY TICKETS TAB ── */}
          {activeTab === 'tickets' && (
            <div className="support-tickets-panel">
              {!isAuthenticated ? (
                <div className="tickets-empty-state">
                  <span className="tickets-empty-icon">🔒</span>
                  <p>Please <strong>log in</strong> to view your support ticket history.</p>
                </div>
              ) : ticketsLoading ? (
                <div className="tickets-loading">
                  <span className="typing-dot" /><span className="typing-dot" /><span className="typing-dot" />
                  <p>Loading your tickets…</p>
                </div>
              ) : ticketsError ? (
                <div className="tickets-empty-state">
                  <span className="tickets-empty-icon">⚠️</span>
                  <p>{ticketsError}</p>
                  <button className="tickets-retry-btn" onClick={fetchMyTickets}>Retry</button>
                </div>
              ) : myTickets.length === 0 ? (
                <div className="tickets-empty-state">
                  <span className="tickets-empty-icon">📭</span>
                  <p>No support tickets yet.</p>
                  <p className="tickets-hint">Start a chat and describe your issue — we'll create one for you!</p>
                </div>
              ) : (
                <div className="tickets-list">
                  {myTickets.map((ticket) => (
                    <div key={ticket.id} className="ticket-card">
                      <div className="ticket-card-top">
                        <span className="ticket-ref">{ticket.referenceNumber}</span>
                        <span className={`ticket-status-badge status-${ticket.status?.toLowerCase()}`}>
                          {ticket.status}
                        </span>
                      </div>
                      <div className="ticket-subject">{ticket.subject}</div>
                      <div className="ticket-meta">
                        <span className={`ticket-category-pill cat-${ticket.category?.toLowerCase()}`}>{ticket.category}</span>
                        <span className="ticket-date">
                          {new Date(ticket.createdAt).toLocaleString('en-IN', {
                            day: '2-digit',
                            month: 'short',
                            year: 'numeric',
                            hour: '2-digit',
                            minute: '2-digit',
                            hour12: true
                          })}
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}

          {/* ── CHAT TAB ── */}
          {/* Messages Body */}
          {activeTab === 'chat' && (
          <div
            ref={chatContainerRef}
            onScroll={handleMessagesScroll}
            className="support-chat-messages"
            role="log"
            aria-live="polite"
          >
            {messages.map((msg) => (
              <MessageRow key={msg.id} msg={msg} />
            ))}

            {/* Typing indicator — only show when NOT streaming (streaming bot placeholder is already visible) */}
            {loading && !messages.some(m => m.isStreaming) && (
              <div className="support-msg-row msg-bot-row">
                <div className="msg-bot-avatar">🤖</div>
                <div className="msg-bubble-wrap">
                  <div className="msg-bubble bubble-bot bubble-typing">
                    <span className="typing-dot" />
                    <span className="typing-dot" />
                    <span className="typing-dot" />
                  </div>
                </div>
              </div>
            )}

            <div ref={messagesEndRef} />
          </div>
          )}

          {/* Quick Suggestions + Footer — only in Chat tab */}
          {activeTab === 'chat' && <div className="support-quick-suggestions">
            <div className="quick-suggestions-header">
              <span className="quick-suggestions-label">⚡ Suggestions</span>
            </div>
            <div className="quick-chips-scroll">
              {quickPrompts.map((prompt, idx) => (
                <button
                  key={idx}
                  type="button"
                  className="quick-prompt-chip"
                  onClick={() => handleSend(prompt)}
                  disabled={loading}
                  title={prompt}
                >
                  {prompt}
                </button>
              ))}
            </div>
          </div>}

          {/* Chat Input Bar - Pointer & Caret enabled with container click focus */}
          {activeTab === 'chat' && (
          <div
            className="support-chat-footer"
            onClick={() => inputRef.current?.focus()}
          >
            <div
              className="input-bar-container"
              onClick={(e) => {
                e.stopPropagation();
                inputRef.current?.focus();
              }}
            >
              <textarea
                ref={inputRef}
                rows={1}
                className="support-chat-input"
                autoFocus
                placeholder={
                  !isAuthenticated && !guestName
                    ? "Please enter your name or username first..."
                    : "Ask AI Support or report an issue..."
                }
                value={inputMessage}
                onChange={(e) => setInputMessage(e.target.value)}
                onKeyDown={handleKeyDown}
                disabled={loading}
              />
              <button
                type="button"
                className="support-send-btn"
                onClick={() => handleSend()}
                disabled={loading || !inputMessage.trim()}
                aria-label="Send message"
              >
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round">
                  <line x1="22" y1="2" x2="11" y2="13" />
                  <polygon points="22 2 15 22 11 13 2 9 22 2" />
                </svg>
              </button>
            </div>
          </div>
          )}
        </div>
      )}
    </div>
  );
}
