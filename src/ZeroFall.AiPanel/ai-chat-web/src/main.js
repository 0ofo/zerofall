import { createApp } from 'vue'
import App from './App.vue'
import { applyCommand } from './chat-bridge.js'
import JsonViewer from 'vue3-json-viewer'
import 'vue3-json-viewer/dist/vue3-json-viewer.css'
import './style.css'

document.addEventListener('contextmenu', event => event.preventDefault())

createApp(App).use(JsonViewer).mount('#app')

window.zerofallChat = { receive: applyCommand }
window.aiChatReady = true
