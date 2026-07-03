import { reactive } from 'vue'

/** 全局确认框状态（非阻塞，由 <ConfirmDialog /> 渲染）。 */
export const confirmState = reactive({
  visible: false,
  title: '确认',
  message: '',
  confirmText: '确定',
  cancelText: '取消',
  danger: false,
  _resolve: null
})

/**
 * 打开确认对话框，返回 Promise；用户点确定 resolve(true)，取消/遮罩/Esc resolve(false)。
 * @param {{ title?: string, message?: string, confirmText?: string, cancelText?: string, danger?: boolean }} options
 */
export function openConfirm(options = {}) {
  if (confirmState._resolve)
    confirmState._resolve(false)

  const wasOpen = confirmState.visible

  return new Promise(resolve => {
    confirmState.title = options.title ?? '确认'
    confirmState.message = options.message ?? ''
    confirmState.confirmText = options.confirmText ?? '确定'
    confirmState.cancelText = options.cancelText ?? '取消'
    confirmState.danger = !!options.danger
    confirmState._resolve = resolve

    if (wasOpen) {
      confirmState.visible = false
      queueMicrotask(() => {
        confirmState.visible = true
      })
    } else {
      confirmState.visible = true
    }
  })
}

export function closeConfirm(result) {
  const resolve = confirmState._resolve
  if (!resolve)
    return

  confirmState._resolve = null
  confirmState.visible = false
  resolve(result)
}
