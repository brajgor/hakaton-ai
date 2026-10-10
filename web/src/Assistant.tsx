import { FormEvent, useEffect, useRef, useState } from 'react';
import { Bot, ExternalLink, Loader2, Send } from 'lucide-react';
import texts from './i18n/lv.json';

export type AssistantItem = {
  kind: string;
  kindLabel: string;
  code: string;
  title: string;
  text: string;
  source: string;
  url?: string | null;
  details: string[];
  originalLanguage: boolean;
};

export type AssistantAnswer = {
  language: 'lv' | 'ru' | 'en';
  intro: string;
  items: AssistantItem[];
  suggestions: string[];
  disclaimer: string;
  found: boolean;
};

type Message = { id: number; role: 'user'; text: string } | { id: number; role: 'bot'; answer: AssistantAnswer };

type Props = {
  requestId?: string;
  ask: (question: string, requestId?: string) => Promise<AssistantAnswer>;
};

/**
 * Result assistant shown at the end of the e-service. Answers come from the deterministic
 * knowledge base on the API (no LLM): reason codes, VZD encumbrance codes, cadastre marks,
 * Riga zoning, protection zones, nature areas, monuments and legislation snippets.
 */
export function AssistantPanel({ requestId, ask }: Props) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [question, setQuestion] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const nextId = useRef(1);
  const logRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    setMessages([]);
    ask('', requestId)
      .then((answer) => !cancelled && setMessages([{ id: nextId.current++, role: 'bot', answer }]))
      .catch((reason: unknown) => !cancelled && setError(reason instanceof Error ? reason.message : texts.assistant.error));
    return () => {
      cancelled = true;
    };
  }, [requestId, ask]);

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight, behavior: 'smooth' });
  }, [messages, busy]);

  async function send(text: string) {
    const trimmed = text.trim();
    if (!trimmed || busy) {
      return;
    }

    setQuestion('');
    setError('');
    setBusy(true);
    setMessages((current) => [...current, { id: nextId.current++, role: 'user', text: trimmed }]);
    try {
      const answer = await ask(trimmed, requestId);
      setMessages((current) => [...current, { id: nextId.current++, role: 'bot', answer }]);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : texts.assistant.error);
    } finally {
      setBusy(false);
    }
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    void send(question);
  }

  const last = [...messages].reverse().find((message) => message.role === 'bot');
  const suggestions = last?.role === 'bot' ? last.answer.suggestions : [];

  return (
    <section className="assistant block" aria-labelledby="assistant-title">
      <header className="assistantHeader">
        <Bot size={22} aria-hidden="true" />
        <div>
          <h3 id="assistant-title">{texts.assistant.title}</h3>
          <p className="muted">{texts.assistant.lead}</p>
        </div>
      </header>

      <div className="assistantLog" ref={logRef} role="log" aria-live="polite" aria-busy={busy}>
        {messages.map((message) =>
          message.role === 'user' ? (
            <p key={message.id} className="bubble user">
              {message.text}
            </p>
          ) : (
            <BotMessage key={message.id} answer={message.answer} />
          ),
        )}
        {busy && (
          <p className="bubble bot pending">
            <Loader2 className="spin" size={16} aria-hidden="true" /> {texts.assistant.thinking}
          </p>
        )}
      </div>

      {error && (
        <p className="assistantError" role="alert">
          {error}
        </p>
      )}

      {suggestions.length > 0 && (
        <div className="chips suggestions" aria-label={texts.assistant.suggestions}>
          {suggestions.map((suggestion) => (
            <button key={suggestion} type="button" onClick={() => void send(suggestion)} disabled={busy}>
              {suggestion}
            </button>
          ))}
        </div>
      )}

      <form className="assistantForm" onSubmit={onSubmit}>
        <label className="sr-only" htmlFor="assistant-question">
          {texts.assistant.inputLabel}
        </label>
        <input
          id="assistant-question"
          value={question}
          maxLength={500}
          autoComplete="off"
          onChange={(event) => setQuestion(event.target.value)}
          placeholder={texts.assistant.placeholder}
        />
        <button type="submit" className="primary" disabled={busy || question.trim().length === 0}>
          <Send size={18} aria-hidden="true" />
          <span>{texts.assistant.send}</span>
        </button>
      </form>
      {last?.role === 'bot' && <p className="muted assistantDisclaimer">{last.answer.disclaimer}</p>}
    </section>
  );
}

function BotMessage({ answer }: { answer: AssistantAnswer }) {
  return (
    <div className="bubble bot" lang={answer.language}>
      <p>{answer.intro}</p>
      {answer.items.length > 0 && (
        <ul className="answerItems">
          {answer.items.map((item) => (
            <li key={`${item.kind}-${item.code}-${item.title}`}>
              <div className="answerMeta">
                <span className="kind">{item.kindLabel}</span>
                {item.code && item.code !== item.title && item.kind !== 'reason' && item.kind !== 'level' && <code>{item.code}</code>}
              </div>
              <strong lang={item.originalLanguage ? 'lv' : undefined}>{item.title}</strong>
              {item.text && <p lang={item.originalLanguage ? 'lv' : undefined}>{item.text}</p>}
              {item.details.length > 0 && (
                <ul className="details">
                  {item.details.map((detail) => (
                    <li key={detail}>{detail}</li>
                  ))}
                </ul>
              )}
              <small className="source">
                {texts.assistant.source}: {item.source}
                {item.url && (
                  <>
                    {' · '}
                    <a href={item.url} target="_blank" rel="noreferrer">
                      {texts.assistant.open} <ExternalLink size={12} aria-hidden="true" />
                    </a>
                  </>
                )}
              </small>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
