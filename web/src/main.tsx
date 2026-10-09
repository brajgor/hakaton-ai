import React, { useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { AlertTriangle, CheckCircle2, FileCheck2, Landmark, Loader2, MapPin, Search } from 'lucide-react';
import texts from './i18n/lv.json';
import './styles.css';

const apiBase = import.meta.env.VITE_API_URL || 'http://localhost:5000';

type Intent = { id: string; title: string; description: string };
type Address = {
  id: string;
  normalizedAddress: string;
  arCode: string;
  municipality: string;
  latitude: number;
  longitude: number;
};
type Finding = {
  source: string;
  code: string;
  name: string;
  category: string;
  intersects: boolean;
  distanceMeters?: number;
};
type PrecheckResponse = {
  requestId: string;
  level: 'green' | 'yellow' | 'red';
  reasonCodes: string[];
  dataFindings: Finding[];
  nextSteps: string[];
  officialDisclaimer: string;
};
type MockSession = { urn: string; displayName: string; authMethod: string };

function App() {
  const [step, setStep] = useState(0);
  const [session, setSession] = useState<MockSession | null>(null);
  const [intents, setIntents] = useState<Intent[]>([]);
  const [selectedIntent, setSelectedIntent] = useState<string>('');
  const [query, setQuery] = useState('');
  const [addresses, setAddresses] = useState<Address[]>([]);
  const [selectedAddress, setSelectedAddress] = useState<Address | null>(null);
  const [result, setResult] = useState<PrecheckResponse | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string>('');

  useEffect(() => {
    void Promise.all([
      fetchJson<MockSession>('/api/session/mock').then(setSession),
      fetchJson<Intent[]>('/api/intents').then(setIntents),
    ]).catch((error) => setMessage(error.message));
  }, []);

  const selectedIntentModel = useMemo(
    () => intents.find((intent) => intent.id === selectedIntent),
    [intents, selectedIntent],
  );

  async function searchAddresses() {
    if (query.trim().length < 2) {
      setMessage('Ievadiet vismaz 2 simbolus.');
      return;
    }

    setBusy(true);
    setMessage('');
    try {
      const found = await fetchJson<Address[]>(`/api/addresses/search?q=${encodeURIComponent(query.trim())}`);
      setAddresses(found);
      if (found.length === 0) {
        setMessage('Adrese netika atrasta demo datu izgriezumā.');
      }
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Nezināma kļūda.');
    } finally {
      setBusy(false);
    }
  }

  async function runPrecheck() {
    if (!selectedIntent || !selectedAddress) {
      setMessage(!selectedIntent ? texts.noIntent : texts.noAddress);
      return;
    }

    setBusy(true);
    setMessage('');
    try {
      const response = await fetchJson<PrecheckResponse>('/api/precheck', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ intentId: selectedIntent, addressId: selectedAddress.id }),
      });
      setResult(response);
      setStep(2);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'Nezināma kļūda.');
    } finally {
      setBusy(false);
    }
  }

  function goNext() {
    setMessage('');
    if (step === 0 && !selectedIntent) {
      setMessage(texts.noIntent);
      return;
    }
    if (step === 1 && !selectedAddress) {
      setMessage(texts.noAddress);
      return;
    }
    setStep((current) => Math.min(current + 1, 3));
  }

  return (
    <main className="shell">
      <header className="topbar" aria-label="Pakalpojuma galvene">
        <div>
          <p className="eyebrow">Latvija.gov.lv e-pakalpojuma prototips</p>
          <h1>{texts.appTitle}</h1>
          <p className="lead">{texts.appSubtitle}</p>
        </div>
        <div className="session" aria-label={texts.session}>
          <span>{session?.displayName || '...'}</span>
          <small>{session?.authMethod || 'Mock session'}</small>
        </div>
      </header>

      <nav className="stepper" aria-label="Pakalpojuma soļi">
        {texts.steps.map((label, index) => (
          <button
            key={label}
            className={index === step ? 'step active' : index < step ? 'step done' : 'step'}
            type="button"
            onClick={() => index < step && setStep(index)}
            aria-current={index === step ? 'step' : undefined}
          >
            <span>{index + 1}</span>
            {label}
          </button>
        ))}
      </nav>

      {message && (
        <div className="notice" role="alert">
          <AlertTriangle size={20} aria-hidden="true" />
          <span>{message}</span>
        </div>
      )}

      <section className="panel" aria-live="polite">
        {step === 0 && (
          <IntentStep
            intents={intents}
            selectedIntent={selectedIntent}
            onSelect={setSelectedIntent}
          />
        )}
        {step === 1 && (
          <AddressStep
            query={query}
            setQuery={setQuery}
            addresses={addresses}
            selectedAddress={selectedAddress}
            setSelectedAddress={setSelectedAddress}
            onSearch={searchAddresses}
            busy={busy}
          />
        )}
        {step === 2 && (
          <ResultStep
            result={result}
            selectedIntent={selectedIntentModel}
            selectedAddress={selectedAddress}
          />
        )}
        {step === 3 && (
          <SummaryStep
            result={result}
            selectedIntent={selectedIntentModel}
            selectedAddress={selectedAddress}
            session={session}
          />
        )}
      </section>

      <footer className="actions">
        <button type="button" className="secondary" onClick={() => setStep((current) => Math.max(0, current - 1))} disabled={step === 0 || busy}>
          {texts.back}
        </button>
        {step === 1 ? (
          <button type="button" className="primary" onClick={runPrecheck} disabled={busy}>
            {busy ? <Loader2 className="spin" size={18} aria-hidden="true" /> : <FileCheck2 size={18} aria-hidden="true" />}
            {texts.submit}
          </button>
        ) : step === 2 ? (
          <button type="button" className="primary" onClick={() => setStep(3)} disabled={!result}>
            {texts.finish}
          </button>
        ) : step < 3 ? (
          <button type="button" className="primary" onClick={goNext} disabled={busy}>
            {texts.next}
          </button>
        ) : (
          <button type="button" className="primary" onClick={() => window.print()}>
            Drukāt kopsavilkumu
          </button>
        )}
      </footer>
    </main>
  );
}

function IntentStep({ intents, selectedIntent, onSelect }: { intents: Intent[]; selectedIntent: string; onSelect: (id: string) => void }) {
  return (
    <>
      <h2>{texts.actionsTitle}</h2>
      <p className="muted">{texts.actionsLead}</p>
      <fieldset className="choices">
        <legend className="sr-only">{texts.actionsTitle}</legend>
        {intents.map((intent) => (
          <label key={intent.id} className={selectedIntent === intent.id ? 'choice selected' : 'choice'}>
            <input
              type="radio"
              name="intent"
              value={intent.id}
              checked={selectedIntent === intent.id}
              onChange={() => onSelect(intent.id)}
            />
            <span>
              <strong>{intent.title}</strong>
              <small>{intent.description}</small>
            </span>
          </label>
        ))}
      </fieldset>
    </>
  );
}

function AddressStep(props: {
  query: string;
  setQuery: (value: string) => void;
  addresses: Address[];
  selectedAddress: Address | null;
  setSelectedAddress: (address: Address) => void;
  onSearch: () => void;
  busy: boolean;
}) {
  return (
    <>
      <h2>{texts.addressTitle}</h2>
      <p className="muted">{texts.addressLead}</p>
      <div className="searchRow">
        <label>
          <span>{texts.search}</span>
          <input
            value={props.query}
            onChange={(event) => props.setQuery(event.target.value)}
            onKeyDown={(event) => event.key === 'Enter' && props.onSearch()}
            placeholder={texts.searchPlaceholder}
          />
        </label>
        <button type="button" className="secondary iconButton" onClick={props.onSearch} disabled={props.busy}>
          {props.busy ? <Loader2 className="spin" size={18} aria-hidden="true" /> : <Search size={18} aria-hidden="true" />}
          Meklēt
        </button>
      </div>
      <div className="addressList" role="list">
        {props.addresses.map((address) => (
          <button
            key={address.id}
            className={props.selectedAddress?.id === address.id ? 'address selected' : 'address'}
            type="button"
            onClick={() => props.setSelectedAddress(address)}
          >
            <MapPin size={20} aria-hidden="true" />
            <span>
              <strong>{address.normalizedAddress}</strong>
              <small>{address.municipality} · AR {address.arCode}</small>
            </span>
          </button>
        ))}
      </div>
    </>
  );
}

function ResultStep({ result, selectedIntent, selectedAddress }: { result: PrecheckResponse | null; selectedIntent?: Intent; selectedAddress: Address | null }) {
  if (!result) {
    return <p className="muted">Veiciet priekšpārbaudi, lai redzētu rezultātu.</p>;
  }

  return (
    <>
      <h2>{texts.resultTitle}</h2>
      <ResultBanner level={result.level} />
      <dl className="kv">
        <dt>Darbība</dt>
        <dd>{selectedIntent?.title}</dd>
        <dt>Adrese</dt>
        <dd>{selectedAddress?.normalizedAddress}</dd>
      </dl>
      <FindingList findings={result.dataFindings} />
      <ReasonList codes={result.reasonCodes} />
      <NextSteps steps={result.nextSteps} />
      <p className="disclaimer">{result.officialDisclaimer}</p>
    </>
  );
}

function SummaryStep({ result, selectedIntent, selectedAddress, session }: { result: PrecheckResponse | null; selectedIntent?: Intent; selectedAddress: Address | null; session: MockSession | null }) {
  if (!result) {
    return <p className="muted">Kopsavilkums vēl nav sagatavots.</p>;
  }

  return (
    <>
      <h2>{texts.summaryTitle}</h2>
      <div className="summaryCard">
        <ResultBanner level={result.level} />
        <dl className="kv">
          <dt>Priekšpārbaudes Nr.</dt>
          <dd>{result.requestId}</dd>
          <dt>Lietotājs</dt>
          <dd>{session?.urn}</dd>
          <dt>Darbība</dt>
          <dd>{selectedIntent?.title}</dd>
          <dt>Adrese</dt>
          <dd>{selectedAddress?.normalizedAddress}</dd>
        </dl>
        <FindingList findings={result.dataFindings} />
        <NextSteps steps={result.nextSteps} />
        <p className="disclaimer">{result.officialDisclaimer}</p>
        <p className="muted">{texts.officialPath}</p>
      </div>
    </>
  );
}

function ResultBanner({ level }: { level: PrecheckResponse['level'] }) {
  const icon = level === 'green' ? <CheckCircle2 size={24} aria-hidden="true" /> : <AlertTriangle size={24} aria-hidden="true" />;
  return (
    <div className={`result ${level}`}>
      {icon}
      <strong>{texts[level]}</strong>
    </div>
  );
}

function FindingList({ findings }: { findings: Finding[] }) {
  return (
    <section className="block">
      <h3>{texts.findings}</h3>
      {findings.length === 0 ? (
        <p className="muted">Atrastie ierobežojumi nav konstatēti demo datu izgriezumā.</p>
      ) : (
        <ul className="cards">
          {findings.map((finding) => (
            <li key={`${finding.source}-${finding.code}`}>
              <Landmark size={18} aria-hidden="true" />
              <span>
                <strong>{finding.name}</strong>
                <small>
                  {finding.category} · {finding.intersects ? 'sakrīt ar adresi' : `${finding.distanceMeters ?? '?'} m tuvumā`}
                </small>
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function ReasonList({ codes }: { codes: string[] }) {
  return (
    <section className="block">
      <h3>{texts.reasonCodes}</h3>
      <div className="chips">
        {codes.map((code) => (
          <span key={code}>{code}</span>
        ))}
      </div>
    </section>
  );
}

function NextSteps({ steps }: { steps: string[] }) {
  return (
    <section className="block">
      <h3>{texts.nextSteps}</h3>
      <ol className="nextSteps">
        {steps.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ol>
    </section>
  );
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, init);
  if (!response.ok) {
    const payload = await response.json().catch(() => null);
    throw new Error(payload?.message || `API kļūda: ${response.status}`);
  }
  return response.json() as Promise<T>;
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
