import { useState, useEffect } from 'react';
import './BrandSplash.css';

export default function BrandSplash({ onComplete }) {
  const [fading, setFading] = useState(false);
  const [done, setDone] = useState(false);

  useEffect(() => {
    // 1.1s display, then 400ms fade transition
    const fadeTimer = setTimeout(() => {
      setFading(true);
    }, 1100);

    const doneTimer = setTimeout(() => {
      setDone(true);
      if (onComplete) onComplete();
    }, 1500);

    return () => {
      clearTimeout(fadeTimer);
      clearTimeout(doneTimer);
    };
  }, [onComplete]);

  if (done) return null;

  return (
    <div 
      className={`brand-splash-overlay ${fading ? 'fade-out' : ''}`}
      onClick={() => { setFading(true); setTimeout(() => { setDone(true); if (onComplete) onComplete(); }, 200); }}
    >
      <div className="brand-splash-content">
        {/* Animated 3D Glowing Orb */}
        <div className="brand-orb-container">
          <div className="brand-orb-glow"></div>
          <div className="brand-orb-ring ring-1"></div>
          <div className="brand-orb-ring ring-2"></div>
          <div className="brand-orb-core">
            <span className="brand-orb-icon">⚡</span>
          </div>
        </div>

        {/* Shimmering Brand Title */}
        <h1 className="brand-splash-title">
          Chat<span className="title-accent">App</span>
        </h1>

        {/* Tagline */}
        <p className="brand-splash-tagline">
          NEXT-GEN REAL-TIME MESSAGING
        </p>

        {/* Dynamic Energy Bar */}
        <div className="brand-energy-track">
          <div className="brand-energy-bar"></div>
        </div>
      </div>
    </div>
  );
}
