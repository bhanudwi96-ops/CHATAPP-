import React, { useState, useEffect } from 'react';
import { useAuth } from '../../context/AuthContext';

export default function SupportModal({ isOpen, onClose }) {
  const { user } = useAuth();
  const [category, setCategory] = useState(0);
  const [subject, setSubject] = useState('');
  const [message, setMessage] = useState('');
  const [userEmail, setUserEmail] = useState('');
  const [userName, setUserName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [ticketResult, setTicketResult] = useState(null);
  const [error, setError] = useState('');

  useEffect(() => {
    if (isOpen) {
      setUserEmail(user?.email || '');
      setUserName(user?.displayName || user?.username || '');
      setCategory(0);
      setSubject('');
      setMessage('');
      setTicketResult(null);
      setError('');
    }
  }, [isOpen, user]);

  if (!isOpen) return null;

  const handleSubmit = async (e) => {
    e.preventDefault();
    setSubmitting(true);
    setError('');

    try {
      const token = sessionStorage.getItem('token');
      const res = await fetch('/api/support/ticket', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { Authorization: `Bearer ${token}` } : {})
        },
        body: JSON.stringify({
          userEmail,
          userName: userName || user?.displayName || user?.username || 'Valued User',
          category: parseInt(category, 10),
          subject,
          message
        })
      });

      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        let errorMsg = data.error || data.title;
        if (data.errors && typeof data.errors === 'object') {
          const firstKey = Object.keys(data.errors)[0];
          if (firstKey && Array.isArray(data.errors[firstKey]) && data.errors[firstKey].length > 0) {
            errorMsg = data.errors[firstKey][0];
          }
        }
        throw new Error(errorMsg || 'Failed to submit ticket. Please verify inputs.');
      }

      const data = await res.json();
      setTicketResult(data);
    } catch (err) {
      setError(err.message || 'An unexpected error occurred.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-overlay-glass" onClick={onClose}>
      <div 
        className="modal-card-premium support-modal-card" 
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="modal-header-premium">
          <div className="modal-header-left">
            <div className="modal-icon-badge support-badge-gradient">
              <span>🎧</span>
            </div>
            <div>
              <h3 className="modal-title-premium">Customer Support</h3>
              <p className="modal-subtitle-premium">
                Submit an inquiry or report an issue directly to our team
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

        {/* Body Content */}
        {ticketResult ? (
          <div className="support-success-view">
            <div className="support-success-icon">✓</div>
            <h4 className="support-success-title">Support Ticket Created</h4>
            <div className="support-ref-pill">
              <span>Reference Number:</span>
              <span className="support-ref-code">{ticketResult.referenceNumber}</span>
            </div>
            <p className="support-success-desc">
              We've dispatched a confirmation email to <strong>{ticketResult.userEmail}</strong>. Our team will review your inquiry and get back to you shortly.
            </p>
            <button
              type="button"
              className="btn-modal-primary"
              onClick={() => { setTicketResult(null); onClose(); }}
              style={{ minWidth: '140px' }}
            >
              Done
            </button>
          </div>
        ) : (
          <form onSubmit={handleSubmit} className="modal-form-wrapper">
            <div className="modal-body-scrollable">
              {error && (
                <div className="modal-alert-error">
                  <span className="alert-icon">⚠️</span>
                  <span>{error}</span>
                </div>
              )}

              {/* Name & Email Row */}
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '14px' }}>
                <div className="form-group-premium">
                  <label className="form-label-premium">Your Name</label>
                  <div className="input-with-icon-wrapper">
                    <span className="input-prefix-icon">👤</span>
                    <input
                      type="text"
                      required
                      placeholder="e.g. Jane Doe"
                      value={userName}
                      onChange={(e) => setUserName(e.target.value)}
                      className="input-premium"
                    />
                  </div>
                </div>

                <div className="form-group-premium">
                  <label className="form-label-premium">Your Email</label>
                  <div className="input-with-icon-wrapper">
                    <span className="input-prefix-icon">✉️</span>
                    <input
                      type="email"
                      required
                      placeholder="name@domain.com"
                      value={userEmail}
                      onChange={(e) => setUserEmail(e.target.value)}
                      className="input-premium"
                    />
                  </div>
                </div>
              </div>

              {/* Category */}
              <div className="form-group-premium">
                <label className="form-label-premium">Category</label>
                <div className="input-with-icon-wrapper">
                  <span className="input-prefix-icon">🏷️</span>
                  <select
                    value={category}
                    onChange={(e) => setCategory(e.target.value)}
                    className="select-premium"
                  >
                    <option value={0}>General Inquiry</option>
                    <option value={1}>Technical / Bug Report</option>
                    <option value={2}>Account & Login Help</option>
                    <option value={3}>Billing & Subscription</option>
                    <option value={4}>Feature Request</option>
                  </select>
                </div>
              </div>

              {/* Subject */}
              <div className="form-group-premium">
                <label className="form-label-premium">Subject</label>
                <div className="input-with-icon-wrapper">
                  <span className="input-prefix-icon">📝</span>
                  <input
                    type="text"
                    required
                    minLength={3}
                    maxLength={150}
                    placeholder="Brief summary of your inquiry (min 3 chars)"
                    value={subject}
                    onChange={(e) => setSubject(e.target.value)}
                    className="input-premium"
                  />
                </div>
              </div>

              {/* Message */}
              <div className="form-group-premium">
                <label className="form-label-premium">Message</label>
                <div className="input-with-icon-wrapper">
                  <span className="input-prefix-icon" style={{ top: '14px' }}>💬</span>
                  <textarea
                    required
                    rows={4}
                    minLength={10}
                    maxLength={3000}
                    placeholder="Describe your issue or feedback in detail (min 10 chars)..."
                    value={message}
                    onChange={(e) => setMessage(e.target.value)}
                    className="textarea-premium"
                  />
                </div>
                <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: '4px' }}>
                  <span style={{ fontSize: '11px', color: '#94a3b8' }}>Please provide as much detail as possible</span>
                  <span style={{ 
                    fontSize: '11px', 
                    color: message.trim().length < 10 ? '#ef4444' : '#10b981', 
                    fontWeight: '600' 
                  }}>
                    {message.trim().length}/10 min chars
                  </span>
                </div>
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
                disabled={submitting || subject.trim().length < 3 || message.trim().length < 10}
              >
                {submitting ? 'Submitting...' : 'Send Request'}
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
