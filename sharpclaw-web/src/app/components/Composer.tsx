import type { FormEvent, KeyboardEvent } from 'react'

type ComposerProps = {
  prompt: string
  isSending: boolean
  isSessionProcessing: boolean
  error: string | null
  onPromptChange: (value: string) => void
  onSubmit: (event: FormEvent<HTMLFormElement>) => void
}

export function Composer({ prompt, isSending, isSessionProcessing, error, onPromptChange, onSubmit }: ComposerProps) {
  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return
    }

    event.preventDefault()
    event.currentTarget.form?.requestSubmit()
  }

  return (
    <form className="composer" onSubmit={onSubmit}>
      <textarea
        className="composer-input"
        value={prompt}
        onChange={(e) => onPromptChange(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder="Write a message to the agent..."
        rows={3}
        disabled={isSessionProcessing}
      />
      <div className="composer-footer">
        {error && <span className="error-text">{error}</span>}
        <button type="submit" className="button primary" disabled={isSending || isSessionProcessing || prompt.trim().length === 0}>
          {isSending ? 'Sending...' : isSessionProcessing ? 'Processing...' : 'Send'}
        </button>
      </div>
    </form>
  )
}
