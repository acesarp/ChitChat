import { useEffect, useState } from 'react'

export function avatarUrl(userName: string, version?: number): string {
  const base = `${import.meta.env.BASE_URL}api/avatar/${encodeURIComponent(userName)}`
  return version ? `${base}?v=${version}` : base
}

function initialsOf(userName: string): string {
  const trimmed = userName.trim()
  return trimmed ? trimmed[0].toUpperCase() : '?'
}

interface AvatarProps {
  userName: string
  src: string
  size?: number
}

export function Avatar({ userName, src, size = 38 }: AvatarProps) {
  const [broken, setBroken] = useState(false)

  // A re-upload changes `src` (new version query param) -- reset so the new image gets a
  // fresh chance to load instead of sticking on a previous load failure.
  useEffect(() => {
    setBroken(false)
  }, [src])

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
      onError={() => setBroken(true)}
    />
  )
}
