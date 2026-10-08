import { useState } from 'react'

function initialsOf(userName: string): string {
    const trimmed = userName.trim();
    return trimmed ? trimmed[0].toUpperCase() : '?';
}

interface AvatarProps {
    userName: string;
    src: string;
    size?: number;
}

export function Avatar({ userName, src, size = 38 }: AvatarProps) {
  // Remember *which* src failed rather than a plain flag: a re-upload changes `src` (new
  // version query param), so the new image automatically gets a fresh chance to load.
    const [brokenSrc, setBrokenSrc] = useState<string | null>(null);
    const broken = brokenSrc === src;

  if (broken) {
    return (
      <span className="avatar avatar-fallback" style={{ width: size, height: size, fontSize: size * 0.45 }}>
        {initialsOf(userName)}
      </span>
    )
  }

  return (
    <img
      className="avatar"
      src={src}
      alt={userName}
      style={{ width: size, height: size }}
      onError={() => setBrokenSrc(src)}
    />
  )
}
