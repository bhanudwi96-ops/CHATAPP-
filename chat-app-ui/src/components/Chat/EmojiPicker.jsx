import React, { useState, useEffect, useRef } from 'react';

const EMOJI_CATEGORIES = [
  {
    name: 'Smileys',
    icon: '😀',
    emojis: [
      '😀', '😃', '😄', '😁', '😆', '😅', '😂', '🤣', '😊', '😇',
      '🙂', '🙃', '😉', '😌', '😍', '🥰', '😘', '😗', '😙', '😚',
      '😋', '😛', '😝', '😜', '🤪', '🤨', '🧐', '🤓', '😎', '🥸',
      '🤩', '🥳', '😏', '😒', '😞', '😔', '😟', '😕', '🙁', '☹️',
      '😣', '😖', '😫', '😩', '🥺', '😢', '😭', '😤', '😠', '😡',
      '🤬', '🤯', '😳', '🥵', '🥶', '😱', '😨', '😰', '😥', '😓',
      '🤗', '🤔', '🤭', '🤫', '🤥', '😶', '😐', '😑', '😬', '🙄',
      '😯', '😦', '😧', '😮', '😲', '🥱', '😴', '🤤', '😪', '😵',
      '🤐', '🥴', '🤢', '🤮', '🤧', '😷', '🤒', '🤕', '🤑', '🤠'
    ]
  },
  {
    name: 'Gestures',
    icon: '👍',
    emojis: [
      '👍', '👎', '👌', '✌️', '🤞', '🤟', '🤘', '🤙', '👈', '👉',
      '👆', '🖕', '👇', '☝️', '✋', '🤚', '🖐️', '🖖', '👋', '🤙',
      '💪', '🦾', '👏', '🙌', '👐', '🤲', '🤝', '🙏', '✍️', '💅'
    ]
  },
  {
    name: 'Hearts',
    icon: '❤️',
    emojis: [
      '❤️', '🧡', '💛', '💚', '💙', '💜', '🖤', '🤍', '🤎', '💔',
      '❣️', '💕', '💞', '💓', '💗', '💖', '💘', '💝', '💟', '💌'
    ]
  },
  {
    name: 'Reactions',
    icon: '🔥',
    emojis: [
      '🔥', '✨', '🎉', '🎊', '💯', '🚀', '⭐', '🌟', '💥', '🎈',
      '🎁', '🏆', '🎯', '💡', '💬', '👀', '🍿', '☕', '🍻', '⚡'
    ]
  }
];

const EmojiPicker = ({ onSelectEmoji, onClose }) => {
  const [activeCategory, setActiveCategory] = useState(0);
  const [search, setSearch] = useState('');
  const pickerRef = useRef(null);

  useEffect(() => {
    const handleClickOutside = (e) => {
      if (pickerRef.current && !pickerRef.current.contains(e.target)) {
        onClose();
      }
    };
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, [onClose]);

  const allFilteredEmojis = search
    ? EMOJI_CATEGORIES.flatMap((c) => c.emojis)
    : EMOJI_CATEGORIES[activeCategory].emojis;

  return (
    <div className="emoji-picker-popover" ref={pickerRef}>
      <div className="emoji-picker-header">
        <div className="emoji-category-tabs">
          {EMOJI_CATEGORIES.map((cat, idx) => (
            <button
              type="button"
              key={cat.name}
              className={`category-tab-btn ${!search && activeCategory === idx ? 'active' : ''}`}
              onClick={() => {
                setActiveCategory(idx);
                setSearch('');
              }}
              title={cat.name}
            >
              {cat.icon}
            </button>
          ))}
        </div>
      </div>

      <div className="emoji-grid-container">
        <div className="emoji-grid">
          {allFilteredEmojis.map((emoji, index) => (
            <button
              type="button"
              key={`${emoji}-${index}`}
              className="emoji-btn"
              onClick={(e) => {
                e.preventDefault();
                e.stopPropagation();
                onSelectEmoji(emoji);
              }}
            >
              {emoji}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
};

export default EmojiPicker;
