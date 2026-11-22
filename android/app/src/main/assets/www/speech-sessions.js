// A native recognition session belongs to the widget that started it.
export const speechSessions = new Map();
window.onAndroidSpeech = (kind, text, id) => speechSessions.get(id)?.(kind, text);
