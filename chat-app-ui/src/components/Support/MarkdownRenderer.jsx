import React, { useMemo } from 'react';
import { marked } from 'marked';
import DOMPurify from 'dompurify';

// Configure marked for safe, clean output
marked.setOptions({
  breaks: true,        // Convert single \n to <br>
  gfm: true,           // GitHub Flavoured Markdown
  pedantic: false,
});

/**
 * Renders a markdown string safely into HTML inside a chat bubble.
 * All HTML is sanitised with DOMPurify to prevent XSS.
 */
export default function MarkdownRenderer({ content, className = '' }) {
  const html = useMemo(() => {
    if (!content) return '';
    try {
      const raw = marked.parse(content);
      return DOMPurify.sanitize(raw, {
        ALLOWED_TAGS: [
          'p', 'br', 'strong', 'em', 'u', 's', 'code', 'pre',
          'ul', 'ol', 'li', 'blockquote', 'a', 'h1', 'h2', 'h3',
          'h4', 'h5', 'h6', 'hr', 'span',
        ],
        ALLOWED_ATTR: ['href', 'target', 'rel', 'class'],
        FORCE_BODY: false,
      });
    } catch {
      return content; // Fallback to raw text if parsing fails
    }
  }, [content]);

  return (
    <div
      className={`markdown-body ${className}`}
      // eslint-disable-next-line react/no-danger
      dangerouslySetInnerHTML={{ __html: html }}
    />
  );
}
