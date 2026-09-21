import * as signalR from '@microsoft/signalr';

// SignalR Hub URL
const HUB_URL = 'https://localhost:7172/chatHub';

class SignalRService {
  constructor() {
    this.connection = null;
    this.isConnected = false;
    this.eventHandlers = new Map(); // eventName -> Set of callbacks
  }

  // Register an event listener (persists across reconnects and binds before start)
  _registerHandler(eventName, callback) {
    if (!this.eventHandlers.has(eventName)) {
      this.eventHandlers.set(eventName, new Set());
    }
    this.eventHandlers.get(eventName).add(callback);

    if (this.connection) {
      this.connection.off(eventName);
      this.connection.on(eventName, (...args) => {
        const handlers = this.eventHandlers.get(eventName);
        if (handlers) {
          handlers.forEach((cb) => {
            try {
              cb(...args);
            } catch (err) {
              console.error(`Error in SignalR handler for ${eventName}:`, err);
            }
          });
        }
      });
    }
  }

  // Remove a specific handler for cleanup (call on component unmount)
  removeHandler(eventName, callback) {
    const handlers = this.eventHandlers.get(eventName);
    if (handlers) {
      handlers.delete(callback);
      if (handlers.size === 0) {
        this.eventHandlers.delete(eventName);
        if (this.connection) {
          this.connection.off(eventName);
        }
      }
    }
  }

  _bindAllHandlers() {
    if (!this.connection) return;

    // Bind all registered handlers to the HubConnection
    const standardEvents = [
      'userOnline',
      'userOffline',
      'receiveMessage',
      'receiveMessageNotification',
      'messageEdited',
      'messageDeleted',
      'groupUpdated',
      'userTyping',
      'messageRead',
      'conversationRead',
      'messageReactionUpdated',
      'ticketResolved',         // AI / agent resolves a support ticket
    ];

    standardEvents.forEach((eventName) => {
      this.connection.off(eventName);
      this.connection.on(eventName, (...args) => {
        const handlers = this.eventHandlers.get(eventName);
        if (handlers) {
          handlers.forEach((cb) => {
            try {
              cb(...args);
            } catch (err) {
              console.error(`Error in SignalR handler for ${eventName}:`, err);
            }
          });
        }
      });
    });
  }

  // Connect to SignalR hub with JWT token
  async connect(token) {
    try {
      if (this.connection && this.isConnected) {
        return true;
      }

      console.log('🔌 Connecting to SignalR at:', HUB_URL);

      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL, {
          accessTokenFactory: () => token,
        })
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .configureLogging(signalR.LogLevel.Information)
        .build();

      // Bind all handlers BEFORE starting connection
      this._bindAllHandlers();

      // Set up reconnection handlers
      this.connection.onreconnecting(() => {
        console.log('⏳ SignalR reconnecting...');
        this.isConnected = false;
      });

      this.connection.onreconnected(() => {
        console.log('✅ SignalR reconnected!');
        this.isConnected = true;
      });

      this.connection.onclose(() => {
        console.log('🔌 SignalR disconnected');
        this.isConnected = false;
      });

      await this.connection.start();
      this.isConnected = true;
      console.log('✅ SignalR Connected Successfully!');

      return true;
    } catch (error) {
      console.error('❌ SignalR Connection Error:', error);
      this.isConnected = false;
      return false;
    }
  }

  // Disconnect from hub
  async disconnect() {
    if (this.connection) {
      console.log('📡 Disconnecting SignalR...');
      try {
        await this.connection.stop();
      } catch (err) {}
      this.connection = null;
      this.isConnected = false;
      console.log('✅ SignalR disconnected');
    }
  }

  // Send a message with optional attachments and quoted reply
  async sendMessage(
    conversationId,
    content,
    type = 0,
    attachmentUrl = null,
    fileName = null,
    fileSize = null,
    replyToMessageId = null
  ) {
    if (!this.isConnected) {
      throw new Error('SignalR not connected');
    }

    await this.connection.invoke('SendMessage', {
      conversationId,
      content,
      type,
      attachmentUrl,
      fileName,
      fileSize,
      replyToMessageId,
    });
  }

  // Edit a message
  async editMessage(messageId, newContent) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('EditMessage', messageId, newContent);
    } catch (error) {
      console.error('Error editing message via SignalR:', error);
    }
  }

  // Delete a message for everyone
  async deleteMessage(messageId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('DeleteMessage', messageId);
    } catch (error) {
      console.error('Error deleting message via SignalR:', error);
    }
  }

  // Notify group updated
  async notifyGroupUpdated(conversationId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('NotifyGroupUpdated', conversationId);
    } catch (error) {
      console.error('Error notifying group updated:', error);
    }
  }

  // React to a message with an emoji
  async reactToMessage(messageId, emoji) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('ReactToMessage', messageId, emoji);
    } catch (error) {
      console.error('Error reacting to message:', error);
    }
  }

  // Send typing indicator
  async sendTyping(conversationId, isTyping) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('Typing', conversationId, isTyping);
    } catch (error) {
      console.error('Error sending typing indicator:', error);
    }
  }

  // Mark message as read
  async markAsRead(messageId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('MarkAsRead', messageId);
    } catch (error) {
      console.error('Error marking message as read:', error);
    }
  }

  // Mark all messages in a conversation as read
  async markConversationAsRead(conversationId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('MarkConversationAsRead', conversationId);
    } catch (error) {
      console.error('Error marking conversation as read:', error);
    }
  }

  // Join conversation room
  async joinConversation(conversationId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('JoinConversation', conversationId);
    } catch (error) {
      console.error('Error joining conversation:', error);
    }
  }

  // Leave conversation room
  async leaveConversation(conversationId) {
    if (!this.isConnected) return;
    try {
      await this.connection.invoke('LeaveConversation', conversationId);
    } catch (error) {
      console.error('Error leaving conversation:', error);
    }
  }

  // Event Listeners
  onReceiveMessage(callback) {
    this._registerHandler('receiveMessage', callback);
  }

  onReceiveMessageNotification(callback) {
    this._registerHandler('receiveMessageNotification', callback);
  }

  onMessageEdited(callback) {
    this._registerHandler('messageEdited', callback);
  }

  onMessageDeleted(callback) {
    this._registerHandler('messageDeleted', callback);
  }

  onGroupUpdated(callback) {
    this._registerHandler('groupUpdated', callback);
  }

  onMessageReactionUpdated(callback) {
    this._registerHandler('messageReactionUpdated', callback);
  }

  onUserTyping(callback) {
    this._registerHandler('userTyping', callback);
  }

  onUserOnline(callback) {
    this._registerHandler('userOnline', callback);
  }

  onUserOffline(callback) {
    this._registerHandler('userOffline', callback);
  }

  onMessageRead(callback) {
    this._registerHandler('messageRead', callback);
  }

  onConversationRead(callback) {
    this._registerHandler('conversationRead', callback);
  }

  /** Fired when backend resolves a support ticket for this user */
  onTicketResolved(callback) {
    this._registerHandler('ticketResolved', callback);
  }
}

// Export singleton instance
const signalRService = new SignalRService();
export default signalRService;
