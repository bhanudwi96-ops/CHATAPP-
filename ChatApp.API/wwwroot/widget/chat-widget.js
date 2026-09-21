/**
 * ChatApp AI Customer Support Chatbot Widget
 * Embeddable vanilla JavaScript widget
 */
(function () {
  // Prevent double initialization
  if (window.__ChatAppWidgetLoaded) return;
  window.__ChatAppWidgetLoaded = true;

  // Configuration
  const API_BASE = window.CHAT_WIDGET_API_BASE || window.location.origin;
  const CUSTOMER_ID = window.CHAT_WIDGET_CUSTOMER_ID || 1;

  // State
  let sessionId = null;
  let isInitializing = false;
  let isSending = false;
  let isOpen = false;

  // DOM Elements Template
  function initWidgetDom() {
    // 1. Create floating action trigger
    const trigger = document.createElement("button");
    trigger.id = "chat-widget-trigger";
    trigger.className = "chat-widget-trigger";
    trigger.setAttribute("aria-label", "Open customer support chat");
    trigger.innerHTML = `
      <svg class="chat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"></path>
      </svg>
      <svg class="close-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <line x1="18" y1="6" x2="6" y2="18"></line>
        <line x1="6" y1="6" x2="18" y2="18"></line>
      </svg>
    `;

    // 2. Create Chat Window
    const chatWindow = document.createElement("div");
    chatWindow.id = "chat-widget-window";
    chatWindow.className = "chat-widget-window";
    chatWindow.innerHTML = `
      <div class="chat-widget-header">
        <div class="chat-header-info">
          <div class="chat-avatar-wrapper">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M12 2a2 2 0 0 1 2 2v2a2 2 0 0 1-2 2 2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z"></path>
              <rect x="4" y="8" width="16" height="12" rx="2"></rect>
              <circle cx="9" cy="13" r="1"></circle>
              <circle cx="15" cy="13" r="1"></circle>
              <path d="M9 17h6"></path>
            </svg>
            <span class="chat-status-dot"></span>
          </div>
          <div class="chat-header-text">
            <h3>Support Assistant</h3>
            <p>AI Powered • Ready to help</p>
          </div>
        </div>
        <div class="chat-header-actions">
          <button id="chat-close-btn" aria-label="Close chat">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <line x1="18" y1="6" x2="6" y2="18"></line>
              <line x1="6" y1="6" x2="18" y2="18"></line>
            </svg>
          </button>
        </div>
      </div>

      <div class="chat-widget-body" id="chat-widget-body">
        <div class="chat-message bot">
          <div class="chat-bubble">
            Hello! 👋 How can I help you with your account, billing, or technical questions today?
          </div>
          <span class="chat-time">Just now</span>
        </div>
      </div>

      <div class="chat-widget-footer">
        <form id="chat-input-form" class="chat-input-wrapper">
          <input type="text" id="chat-message-input" placeholder="Type a message or issue..." autocomplete="off" />
          <button type="submit" id="chat-send-btn" class="chat-send-btn" aria-label="Send message">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <line x1="22" y1="2" x2="11" y2="13"></line>
              <polygon points="22 2 15 22 11 13 2 9 22 2"></polygon>
            </svg>
          </button>
        </form>
      </div>
    `;

    document.body.appendChild(trigger);
    document.body.appendChild(chatWindow);

    // Event Listeners
    trigger.addEventListener("click", toggleChat);
    document.getElementById("chat-close-btn").addEventListener("click", toggleChat);
    document.getElementById("chat-input-form").addEventListener("submit", handleSendMessage);
  }

  // Toggle chat window open/closed
  async function toggleChat() {
    isOpen = !isOpen;
    const trigger = document.getElementById("chat-widget-trigger");
    const chatWindow = document.getElementById("chat-widget-window");
    const input = document.getElementById("chat-message-input");

    if (isOpen) {
      trigger.classList.add("open");
      chatWindow.classList.add("open");
      input.focus();

      // Ensure session exists
      if (!sessionId && !isInitializing) {
        await createSession();
      }
    } else {
      trigger.classList.remove("open");
      chatWindow.classList.remove("open");
    }
  }

  // Auto-create session on first open
  async function createSession() {
    isInitializing = true;
    try {
      const response = await fetch(`${API_BASE}/api/sessions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ customerId: CUSTOMER_ID })
      });

      if (response.ok) {
        const data = await response.json();
        sessionId = data.sessionId;
        console.log("[ChatWidget] Session initialized:", sessionId);
      } else {
        console.error("[ChatWidget] Failed to initialize session, status:", response.status);
      }
    } catch (err) {
      console.error("[ChatWidget] Error creating session:", err);
    } finally {
      isInitializing = false;
    }
  }

  // Send message handler
  async function handleSendMessage(e) {
    e.preventDefault();
    if (isSending) return;

    const input = document.getElementById("chat-message-input");
    const sendBtn = document.getElementById("chat-send-btn");
    const messageText = input.value.trim();

    if (!messageText) return;

    // Ensure session is ready
    if (!sessionId) {
      await createSession();
      if (!sessionId) {
        appendBotMessage("Connecting to support server... please try again in a second.");
        return;
      }
    }

    // 1. Render User Message
    appendUserMessage(messageText);
    input.value = "";
    isSending = true;
    sendBtn.disabled = true;

    // 2. Render Typing Indicator
    showTypingIndicator();

    try {
      // 3. POST to /api/chat
      const response = await fetch(`${API_BASE}/api/chat`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sessionId: sessionId,
          customerId: CUSTOMER_ID,
          message: messageText
        })
      });

      removeTypingIndicator();

      if (response.ok) {
        const data = await response.json();
        // Render assistant reply
        appendBotMessage(data.reply);

        // If ticket was created, show highlighted ticket confirmation card inline
        if (data.ticketCreated) {
          appendTicketConfirmationCard(data.ticketId);
        }
      } else if (response.status === 429) {
        appendBotMessage("⚠️ You are sending messages too quickly. Please wait a moment before trying again.");
      } else {
        appendBotMessage("⚠️ Our support assistant is currently unavailable. Please try again later.");
      }
    } catch (err) {
      removeTypingIndicator();
      console.error("[ChatWidget] Send message error:", err);
      appendBotMessage("⚠️ Network error. Please check your connection and try again.");
    } finally {
      isSending = false;
      sendBtn.disabled = false;
      input.focus();
    }
  }

  // Append User message bubble
  function appendUserMessage(text) {
    const body = document.getElementById("chat-widget-body");
    const msgDiv = document.createElement("div");
    msgDiv.className = "chat-message user";
    msgDiv.innerHTML = `
      <div class="chat-bubble">${escapeHtml(text)}</div>
      <span class="chat-time">${formatTime(new Date())}</span>
    `;
    body.appendChild(msgDiv);
    scrollToBottom();
  }

  // Append Bot message bubble
  function appendBotMessage(text) {
    const body = document.getElementById("chat-widget-body");
    const msgDiv = document.createElement("div");
    msgDiv.className = "chat-message bot";
    msgDiv.innerHTML = `
      <div class="chat-bubble">${formatBotMessage(text)}</div>
      <span class="chat-time">${formatTime(new Date())}</span>
    `;
    body.appendChild(msgDiv);
    scrollToBottom();
  }

  // Append inline Ticket Created card
  function appendTicketConfirmationCard(ticketId) {
    const body = document.getElementById("chat-widget-body");
    const cardDiv = document.createElement("div");
    cardDiv.className = "chat-ticket-card";
    cardDiv.innerHTML = `
      <div class="chat-ticket-header">
        <span>🎫 Support Ticket Generated</span>
        <span class="chat-ticket-badge">Ticket #${ticketId || "Pending"}</span>
      </div>
      <div class="chat-ticket-body">
        Our human support team has received your ticket details and will follow up with you within 24 hours.
      </div>
    `;
    body.appendChild(cardDiv);
    scrollToBottom();
  }

  // Show typing indicator
  function showTypingIndicator() {
    const body = document.getElementById("chat-widget-body");
    const indicator = document.createElement("div");
    indicator.id = "chat-typing-indicator";
    indicator.className = "chat-typing-indicator";
    indicator.innerHTML = `
      <div class="chat-typing-dot"></div>
      <div class="chat-typing-dot"></div>
      <div class="chat-typing-dot"></div>
    `;
    body.appendChild(indicator);
    scrollToBottom();
  }

  // Remove typing indicator
  function removeTypingIndicator() {
    const indicator = document.getElementById("chat-typing-indicator");
    if (indicator) indicator.remove();
  }

  // Helpers
  function scrollToBottom() {
    const body = document.getElementById("chat-widget-body");
    if (body) {
      body.scrollTop = body.scrollHeight;
    }
  }

  function formatTime(date) {
    return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  }

  function escapeHtml(str) {
    return str
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#039;");
  }

  function formatBotMessage(str) {
    // Preserve linebreaks and basic styling
    const escaped = escapeHtml(str);
    return escaped.replace(/\n/g, "<br>");
  }

  // Initialize once DOM is ready
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initWidgetDom);
  } else {
    initWidgetDom();
  }
})();
