<script setup>
import { nextTick, ref, watch } from 'vue'
import { closeConfirm, confirmState } from './confirm-dialog.js'

const dialogEl = ref(null)

watch(
  () => confirmState.visible,
  async visible => {
    await nextTick()
    const el = dialogEl.value
    if (!el) return

    if (visible) {
      if (!el.open) el.showModal()
      return
    }

    if (el.open) el.close()
  }
)

function onDialogClose() {
  closeConfirm(dialogEl.value?.returnValue === 'confirm')
}
</script>

<template>
  <div v-if="confirmState.visible" class="confirm-backdrop" aria-hidden="true"></div>
  <dialog
    ref="dialogEl"
    class="confirm-dialog"
    closedby="any"
    :aria-labelledby="confirmState.title ? 'confirm-title' : undefined"
    aria-describedby="confirm-message"
    @close="onDialogClose"
  >
    <form method="dialog" class="confirm-form">
      <h2 v-if="confirmState.title" id="confirm-title" class="confirm-title">
        {{ confirmState.title }}
      </h2>
      <p id="confirm-message" class="confirm-message">{{ confirmState.message }}</p>
      <div class="confirm-actions">
        <button type="submit" class="confirm-btn cancel" value="cancel" formnovalidate>
          {{ confirmState.cancelText }}
        </button>
        <button
          type="submit"
          class="confirm-btn ok"
          :class="{ danger: confirmState.danger }"
          value="confirm"
        >
          {{ confirmState.confirmText }}
        </button>
      </div>
    </form>
  </dialog>
</template>

<style scoped>
.confirm-backdrop {
  position: fixed;
  inset: 0;
  z-index: 999;
  background: color-mix(in srgb, var(--bg) 48%, transparent);
  pointer-events: none;
}

.confirm-dialog {
  width: min(360px, calc(100vw - 32px));
  max-width: 100%;
  margin: auto;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: 12px;
  background: var(--bg);
  color: var(--text);
  box-shadow:
    0 12px 40px color-mix(in srgb, #000 18%, transparent),
    0 2px 8px color-mix(in srgb, #000 8%, transparent);
}

.confirm-dialog::backdrop {
  background: transparent;
}

.confirm-form {
  margin: 0;
  padding: 16px 18px 14px;
}

.confirm-title {
  margin: 0 0 8px;
  font-size: 15px;
  font-weight: 600;
  line-height: 1.4;
  color: var(--text);
}

.confirm-message {
  margin: 0 0 16px;
  font-size: 13px;
  line-height: 1.55;
  color: var(--text-2);
  white-space: pre-wrap;
}

.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.confirm-btn {
  min-width: 72px;
  padding: 6px 14px;
  border-radius: 8px;
  border: 1px solid transparent;
  font-size: 13px;
  line-height: 1.4;
  cursor: pointer;
  font-family: inherit;
  transition:
    background 0.15s ease,
    border-color 0.15s ease,
    color 0.15s ease;
}

.confirm-btn.cancel {
  border-color: var(--border);
  background: var(--fill);
  color: var(--text);
}

.confirm-btn.cancel:hover {
  border-color: var(--text-2);
}

.confirm-btn.ok {
  background: var(--primary);
  color: #fff;
}

.confirm-btn.ok:hover {
  filter: brightness(1.06);
}

.confirm-btn.ok.danger {
  background: #e34d59;
}

.confirm-btn.ok.danger:hover {
  filter: brightness(1.05);
}
</style>
