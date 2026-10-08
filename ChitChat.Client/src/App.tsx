import { useEffect, useRef, useState, type ChangeEvent, type SubmitEvent } from 'react'
import { useChat, type ChatMessage, type PrivateMessage } from './useChat'
import { Avatar, avatarUrl } from './Avatar'
import { logger } from './logger'
import './App.css'

const USER_NAME_STORAGE_KEY = 'chit-chat.chat.userName'
const ACTIVE_PRIVATE_CHAT_STORAGE_KEY = 'chit-chat.chat.activePrivateChat'
const MAX_AVATAR_BYTES = 1_000_000
const AVATAR_SIZE = 30
const MAX_RECORDING_SECONDS = 60
const MAX_PHOTO_BYTES = 5_000_000 // same cap as the hub's MaxMediaBytes
const ALLOWED_PHOTO_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif']
const PREFERRED_AUDIO_MIME_TYPES = ['audio/webm;codecs=opus', 'audio/webm', 'audio/mp4', 'audio/ogg;codecs=opus']

function readStoredUserName(): string | null {
  try {
    return localStorage.getItem(USER_NAME_STORAGE_KEY)
  } catch {
    return null
  }
}

function readStoredActivePrivateChat(): string | null {
  try {
    return localStorage.getItem(ACTIVE_PRIVATE_CHAT_STORAGE_KEY)
  } catch {
    return null
  }
}

function formatTimestamp(iso: string): string {
    const date = new Date(iso);
    const now = new Date();
    const time = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  if (date.toDateString() === now.toDateString()) {
      return time;
  }
    const day = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    return `${day}, ${time}`;
}

function pickSupportedAudioMimeType(): string | undefined {
  if (typeof MediaRecorder === 'undefined') {
      return undefined;
  }
    return PREFERRED_AUDIO_MIME_TYPES.find((type) => MediaRecorder.isTypeSupported(type));
}

function mediaSrc(msg: ChatMessage | PrivateMessage, media: 'audio' | 'photo', viewer: string): string {
  if ('userName' in msg) {
      return `${import.meta.env.BASE_URL}api/${media}-message/${msg.id}`;
  }
    return `${import.meta.env.BASE_URL}api/private-${media}-message/${msg.id}?viewer=${encodeURIComponent(viewer)}`;
}

function App() {
    const [userName, setUserName] = useState<string | null>(readStoredUserName);
    const [nameInput, setNameInput] = useState('');
    const [messageInput, setMessageInput] = useState('');
    const [activePrivateChat, setActivePrivateChat] = useState<string | null>(readStoredActivePrivateChat);
    const [avatarVersion, setAvatarVersion] = useState(() => Date.now());
    const [avatarUploading, setAvatarUploading] = useState(false);
    const [avatarError, setAvatarError] = useState<string | null>(null);
    const [isRecording, setIsRecording] = useState(false);
    const [recordingSeconds, setRecordingSeconds] = useState(0);
    const [mediaError, setMediaError] = useState<string | null>(null);
    const [isSidebarOpen, setIsSidebarOpen] = useState(false);
  const {
    messages,
    privateMessages,
    onlineUsers,
    isConnected,
    sendMessage,
  } = useChat(userName);

    const messagesEndRef = useRef<HTMLDivElement>(null);
    const avatarInputRef = useRef<HTMLInputElement>(null);
    const photoInputRef = useRef<HTMLInputElement>(null);
    const mediaRecorderRef = useRef<MediaRecorder | null>(null);
    const audioChunksRef = useRef<Blob[]>([]);
    const recordingTimerRef = useRef<number | null>(null);
    const activePrivateChatRef = useRef(activePrivateChat);
    activePrivateChatRef.current = activePrivateChat;

  const activeMessages = activePrivateChat ? privateMessages[activePrivateChat] ?? [] : messages;

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [activeMessages]);

  function openPrivateChat(withUser: string) {
    if (withUser === userName) {
      return;
    }
    setActivePrivateChat(withUser);
    setIsSidebarOpen(false);
    try {
      localStorage.setItem(ACTIVE_PRIVATE_CHAT_STORAGE_KEY, withUser);
    } catch {
      // ignore storage failures (e.g. private browsing) -- session just won't persist
    }
  }

  function backToLobby() {
      setActivePrivateChat(null);
    try {
        localStorage.removeItem(ACTIVE_PRIVATE_CHAT_STORAGE_KEY);
    } catch {
      // ignore
    }
  }

    function handleJoin(event: SubmitEvent<HTMLFormElement>) {
      event.preventDefault();
      const trimmed = nameInput.trim();
    if (trimmed) {
      try {
          localStorage.setItem(USER_NAME_STORAGE_KEY, trimmed);
      } catch {
        // ignore storage failures (e.g. private browsing) -- session just won't persist
      }
        setUserName(trimmed);
    }
  }

  function handleLeave() {
      stopRecording();
    try {
        localStorage.removeItem(USER_NAME_STORAGE_KEY);
        localStorage.removeItem(ACTIVE_PRIVATE_CHAT_STORAGE_KEY);
    } catch {
      // ignore
    }
      setUserName(null);
      setActivePrivateChat(null);
      setIsSidebarOpen(false);
  }

    function handleSend(event: SubmitEvent<HTMLFormElement>) {
      event.preventDefault();
      const trimmed = messageInput.trim();
    if (trimmed) {
      void sendMessage({ kind: 'text', text: trimmed }, activePrivateChat ?? undefined);
      setMessageInput('');
    }
  }

  function triggerAvatarUpload() {
      avatarInputRef.current?.click();
  }

    function triggerPhotoUpload() {
        photoInputRef.current?.click();
    }

    function handlePhotoSelected(event: ChangeEvent<HTMLInputElement>) {
        const file = event.target.files?.[0];
        event.target.value = '';
        if (!file) {
            return;
        }

        setMediaError(null);
        if (!ALLOWED_PHOTO_TYPES.includes(file.type)) {
            setMediaError('Unsupported image type. Use PNG, JPEG, WEBP, or GIF.');
            return;
        }
        if (file.size > MAX_PHOTO_BYTES) {
            setMediaError('Photo must be 5MB or smaller.');
            return;
        }

        void sendMessage({ kind: 'photo', blob: file }, activePrivateChatRef.current ?? undefined);
    }

  async function handleAvatarSelected(event: ChangeEvent<HTMLInputElement>) {
      const file = event.target.files?.[0];
      event.target.value = '';
    if (!file || !userName) {
        return;
    }

    if (file.size > MAX_AVATAR_BYTES) {
        setAvatarError('Image must be smaller than 1MB.');
        return;
    }

      setAvatarUploading(true);
      setAvatarError(null);
    try {
        const formData = new FormData();
        formData.append('userName', userName);
        formData.append('file', file);
      const response = await fetch(`${import.meta.env.BASE_URL}api/avatar`, {
        method: 'POST',
        body: formData,
      })
      if (!response.ok) {
          throw new Error((await response.text()) || 'Upload failed.');
      }
        setAvatarVersion(Date.now());
    } catch (error) {
        logger.warn('Avatar upload failed:', error);
        setAvatarError(error instanceof Error ? error.message : 'Upload failed.');
    } finally {
        setAvatarUploading(false);
    }
  }

  function clearRecordingTimer() {
    if (recordingTimerRef.current !== null) {
        window.clearInterval(recordingTimerRef.current);
        recordingTimerRef.current = null;
    }
  }

  function stopRecording() {
      mediaRecorderRef.current?.stop();
      mediaRecorderRef.current = null;
      clearRecordingTimer();
      setIsRecording(false);
  }

  async function startRecording() {
      setMediaError(null);

    if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
      logger.warn('Voice messages unavailable: no navigator.mediaDevices.getUserMedia in this browser/context.')
      setMediaError('Voice messages need microphone access, which this browser/context does not allow.')
      return
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const mimeType = pickSupportedAudioMimeType()
      const recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined)
      audioChunksRef.current = []

      recorder.ondataavailable = (event) => {
        if (event.data.size > 0) {
          audioChunksRef.current.push(event.data)
        }
      }

      recorder.onstop = () => {
          stream.getTracks().forEach((track) => track.stop());
          const blob = new Blob(audioChunksRef.current, { type: recorder.mimeType || mimeType || 'audio/webm' });
          audioChunksRef.current = [];
          void sendRecordedAudio(blob);
      }

        recorder.start();
        mediaRecorderRef.current = recorder;
        setIsRecording(true);
        setRecordingSeconds(0);
      recordingTimerRef.current = window.setInterval(() => {
        setRecordingSeconds((seconds) => {
            const next = seconds + 1;
          if (next >= MAX_RECORDING_SECONDS) {
              stopRecording();
          }
          return next;
        });
      }, 1000); 
    } catch (error) {
        logger.warn('Could not start voice recording:', error);
        setMediaError('Microphone access was denied or is unavailable.');
    }
  }

  async function sendRecordedAudio(blob: Blob) {
    if (blob.size === 0) {
        return;
    }
      await sendMessage({ kind: 'audio', blob }, activePrivateChatRef.current ?? undefined);
  }

  function toggleRecording() {
        if (isRecording) {
            stopRecording();
        } else {
            void startRecording();
        }
    }

  if (!userName) {
    return (
      <div className="join-screen">
        <form className="join-card" onSubmit={handleJoin}>
          <div className="brand-logo">
            <img src={`${import.meta.env.BASE_URL}logo.png`} alt="Chit Chat" />
          </div>
          <p>Pick a display name to join the lobby.</p>
          <input
            autoFocus
            value={nameInput}
            onChange={(event) => setNameInput(event.target.value)}
            placeholder="Your name"
            maxLength={30}
          />
          <button type="submit">Join chat</button>
        </form>
      </div>
    )
  }

  const privateChatPartners = Object.keys(privateMessages)

    return (
        <div className="chat-layout">
            {isSidebarOpen && <div className="sidebar-backdrop" onClick={() => setIsSidebarOpen(false)} />}

            <aside className={`sidebar ${isSidebarOpen ? 'open' : ''}`}>
                <div className="brand-logo sidebar-brand">
                    <img src={`${import.meta.env.BASE_URL}logo.png`} alt="Chit Chat" />
                </div>
                <h2>Lobby</h2>
                <p className={`status ${isConnected ? 'online' : 'offline'}`}> {isConnected ? 'Connected' : 'Connecting…'} </p>

                <input ref={avatarInputRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif" onChange={handleAvatarSelected} className="avatar-input" />

                <h3>Online ({onlineUsers.length})</h3>
                <ul className="online-users">
                    {onlineUsers.map((name) => name === userName ? (
                        <li key={name}>
                            <span className="user-entry self">
                                <button
                                    type="button"
                                    className="avatar-button"
                                    onClick={triggerAvatarUpload}
                                    disabled={avatarUploading}
                                    title={avatarUploading ? 'Uploading…' : 'Change your avatar'}
                                >
                                    <Avatar userName={name} src={avatarUrl(name, avatarVersion)} size={AVATAR_SIZE} />
                                </button>
                                {name} (you)
                            </span>
                        </li>
                    ) : (
                        <li key={name}>
                            <button
                                type="button"
                                className={`user-entry ${activePrivateChat === name ? 'active' : ''}`}
                                onClick={() => openPrivateChat(name)}
                            >
                                <Avatar userName={name} src={avatarUrl(name)} size={AVATAR_SIZE} />
                                {name}
                            </button>
                        </li>
                    ),
                    )}
                </ul>
                {avatarError && <p className="avatar-error">{avatarError}</p>}

                {privateChatPartners.length > 0 && (
                    <>
                        <h3>Private chats</h3>
                        <ul className="online-users">
                            {privateChatPartners.map((name) => (
                                <li key={name}>
                                    <button type="button" className={`user-entry ${activePrivateChat === name ? 'active' : ''}`} onClick={() => openPrivateChat(name)} >
                                        <Avatar userName={name} src={avatarUrl(name)} size={AVATAR_SIZE} />
                                        {name}
                                    </button>
                                </li>
                            ))}
                        </ul>
                    </>
                )}

                <button type="button" className="leave-button" onClick={handleLeave}> Leave chat </button>
            </aside>

            <main className="chat-main">
                <div className="chat-header">
                    <button
                        type="button"
                        className="menu-button"
                        onClick={() => setIsSidebarOpen(true)}
                        aria-label="Open menu"
                    >
                        ☰
                    </button>
                    {activePrivateChat ? (
                        <>
                            <button type="button" className="back-button" onClick={backToLobby}>
                                ← Lobby
                            </button>
                            <span className="chat-header-title">Private chat with {activePrivateChat}</span>
                        </>
                    ) : (
                        <span className="chat-header-title">Lobby</span>
                    )}
                </div>

                <div className="messages">
                    {activeMessages.map((msg) => {
                        const author = 'userName' in msg ? msg.userName : msg.fromUserName
                        const isSystem = author === 'system'
                        const isOwn = author === userName
                        return (
                            <div key={msg.id} className={`message ${isOwn ? 'own' : ''} ${isSystem ? 'system' : ''}`}>
                                {!isSystem && (
                                    <div className="message-header">
                                        <Avatar userName={author} src={avatarUrl(author, isOwn ? avatarVersion : undefined)} size={AVATAR_SIZE} />
                                        <span className="message-author">{author}</span>
                                        <span className="message-time">{formatTimestamp(msg.sentAt)}</span>
                                    </div>
                                )}
                                {msg.photoContentType ? (
                                    <a href={mediaSrc(msg, 'photo', userName)} target="_blank" rel="noopener noreferrer">
                                        <img className="message-photo" src={mediaSrc(msg, 'photo', userName)} alt={`Photo from ${author}`} loading="lazy" />
                                    </a>
                                ) : msg.audioContentType ? (
                                    <audio controls className="message-audio" src={mediaSrc(msg, 'audio', userName)} />
                                ) : (
                                    <span className="message-text">{msg.message}</span>
                                )}
                            </div>
                        )
                    })}
                    <div ref={messagesEndRef} />
                </div>

                {mediaError && <p className="avatar-error audio-error">{mediaError}</p>}

                <form className="message-form" onSubmit={handleSend}>
                    <input
                        value={messageInput}
                        onChange={(event) => setMessageInput(event.target.value)}
                        placeholder={activePrivateChat ? `Message ${activePrivateChat}…` : 'Type a message…'}
                        maxLength={500}
                        autoFocus
                    />

                    <button type="button" className="photo-button" disabled={!isConnected} title={'Send a photo'} onClick={triggerPhotoUpload}>📸</button>
                    <input ref={photoInputRef} type="file" accept={ALLOWED_PHOTO_TYPES.join(',')} onChange={handlePhotoSelected} className="photo-input" />
                    <button
                        type="button"
                        className={`mic-button ${isRecording ? 'recording' : ''}`}
                        onClick={toggleRecording}
                        disabled={!isConnected}
                        title={isRecording ? 'Stop and send' : 'Record a voice message'}
                    >
                        {isRecording ? `■ ${recordingSeconds}s` : '🎤'}
                    </button>
                    <button type="submit" disabled={!isConnected}> Send </button>
                </form>
            </main>
        </div>
    );
};

export default App;
