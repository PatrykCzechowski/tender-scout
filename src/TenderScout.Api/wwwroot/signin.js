// Sign-in through the token issuer (Keycloak locally) with the authorization code flow and PKCE, written against the
// standard OpenID Connect endpoints. Runs after app.js and uses its functions.
// The access token lives only in the variable below: never in storage, a cookie or the address. A reload starts
// signed out. During the trip to the issuer, session storage holds only the PKCE verifier, the state, the view and an
// unsent question, and they are removed on return.
'use strict';

const SCOPES = 'openid notices:read documents:ask';
const PENDING_KEY = 'tender-scout-sign-in';
const SIGN_IN_TEXT = {
  cancelled: 'Logowanie zostało przerwane. Spróbuj ponownie.',
  failed: 'Nie udało się zalogować. Spróbuj ponownie.',
  unreachable: 'Serwer logowania nie odpowiada. Spróbuj ponownie za chwilę.',
  rejected: 'API nie przyjęło logowania. Spróbuj zalogować się ponownie.'
};

let session = null; // { accessToken, idToken, endSession, fresh } while signed in
let leaving = false; // the browser is on its way to the issuer

const signedIn = () => session !== null;

// Every call to the API goes through here. A 401 means the session has ended: the visitor is sent to sign in again
// and comes back to the same view with the unsent question. The returned promise then never settles, because the
// page is being left. The first 401 right after a sign-in shows an error instead, so the page cannot loop.
async function callApi(path, options, sentQuestion) {
  const response = await fetch(path, { ...options, headers: { ...options.headers, Authorization: 'Bearer ' + session.accessToken } });
  if (!session) return new Promise(() => {}); // signed out while the request ran
  if (response.status !== 401) {
    session.fresh = false;
    return response;
  }
  if (session.fresh) {
    showSignedOut(SIGN_IN_TEXT.rejected);
  } else if (!leaving) {
    const draft = question.value.trim() !== '' ? question.value : sentQuestion || '';
    signIn({ view: currentView(), question: draft, number: number.value });
  }
  return new Promise(() => {});
}

function showSignedOut(message) {
  session = null;
  leaving = false;
  $('app').hidden = true;
  $('signedOut').hidden = false;
  $('signInMsg').textContent = message || '';
  $('signInMsg').hidden = !message;
  $('signIn').disabled = false;
}

const base64Url = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const randomText = () => base64Url(crypto.getRandomValues(new Uint8Array(32)));
const returnAddress = () => location.origin + '/';

// The issuer and the page's client id come from the API's configuration; the endpoints from the issuer's discovery
// document, so nothing here is tied to Keycloak.
async function issuer() {
  const settings = await (await fetch('sign-in-settings')).json();
  const discovery = await (await fetch(settings.authority.replace(/\/$/, '') + '/.well-known/openid-configuration')).json();
  return { clientId: settings.clientId, ...discovery };
}

async function signIn(keep) {
  leaving = true;
  $('signIn').disabled = true;
  try {
    const { clientId, authorization_endpoint: authorize } = await issuer();
    const verifier = randomText(), state = randomText();
    const challenge = base64Url(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier)));
    sessionStorage.setItem(PENDING_KEY, JSON.stringify({ verifier, state, ...keep }));
    location.assign(authorize + '?' + new URLSearchParams({
      response_type: 'code', client_id: clientId, redirect_uri: returnAddress(), scope: SCOPES,
      state, code_challenge: challenge, code_challenge_method: 'S256'
    }));
  } catch {
    showSignedOut(SIGN_IN_TEXT.unreachable);
  }
}

// Back from the issuer: check the state, then exchange the code (with the verifier) for the tokens.
async function finishSignIn(params) {
  const pending = JSON.parse(sessionStorage.getItem(PENDING_KEY) || 'null');
  sessionStorage.removeItem(PENDING_KEY);
  history.replaceState(null, '', returnAddress() + (pending?.view ? '#' + pending.view : location.hash));
  if (!pending || params.get('state') !== pending.state) return showSignedOut(SIGN_IN_TEXT.failed);
  if (!params.has('code')) return showSignedOut(SIGN_IN_TEXT.cancelled);
  try {
    const { clientId, token_endpoint: tokenEndpoint, end_session_endpoint: endSession } = await issuer();
    const response = await fetch(tokenEndpoint, {
      method: 'POST',
      body: new URLSearchParams({
        grant_type: 'authorization_code', code: params.get('code'), redirect_uri: returnAddress(),
        client_id: clientId, code_verifier: pending.verifier
      })
    });
    const tokens = response.ok ? await response.json() : null;
    if (!tokens?.access_token) return showSignedOut(SIGN_IN_TEXT.failed);
    session = { accessToken: tokens.access_token, idToken: tokens.id_token, clientId, endSession, fresh: true };
  } catch {
    return showSignedOut(SIGN_IN_TEXT.unreachable);
  }
  question.value = pending.question || '';
  number.value = pending.number || '';
  updateSend();
  $('signedOut').hidden = true;
  $('app').hidden = false;
  showView();
}

// Ends the session at the issuer too, so the next sign-in asks for the password. A form post keeps the ID token out
// of any address; the issuer then returns to the page, which starts signed out.
function signOut() {
  const form = el('form', { method: 'post', action: session.endSession });
  for (const [name, value] of Object.entries({ client_id: session.clientId, id_token_hint: session.idToken, post_logout_redirect_uri: returnAddress() })) {
    form.append(el('input', { type: 'hidden', name, value }));
  }
  session = null;
  document.body.append(form);
  form.submit();
}

$('signIn').addEventListener('click', () => signIn({ view: currentView() }));
$('signOut').addEventListener('click', signOut);

const returned = new URLSearchParams(location.search);
if (returned.has('state')) finishSignIn(returned); else showSignedOut();
