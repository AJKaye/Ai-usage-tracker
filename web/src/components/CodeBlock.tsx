import { useState } from 'react'

// A snippet with copy-to-clipboard + optional download-as-file. Styled from role
// tokens only. Used across the Integrations page so every tracking method is
// literally copy/paste (or download) ready.
export function CodeBlock({ code, lang, filename }: { code: string; lang?: string; filename?: string }) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(code)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      setCopied(false)
    }
  }

  function download() {
    const blob = new Blob([code], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = filename!
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <div className="codeblock">
      <div className="codeblock-bar">
        <span className="codeblock-lang">{lang ?? 'text'}</span>
        <span className="codeblock-actions">
          {filename && (
            <button className="btn btn-sm" onClick={download} aria-label={`Download ${filename}`}>↓ {filename}</button>
          )}
          <button className="btn btn-sm" onClick={copy} aria-label="Copy to clipboard">
            {copied ? '✓ Copied' : 'Copy'}
          </button>
        </span>
      </div>
      <pre className="codeblock-pre"><code>{code}</code></pre>
    </div>
  )
}
