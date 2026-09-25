import { useState, type FormEvent } from 'react'
import { DESCRIPTION_MAX_LENGTH } from '../../api/types'
import { ProblemMessage } from '../../components/ProblemMessage'
import { useAppDispatch, useAppSelector } from '../../app/hooks'
import { createProblemCleared, submitOrder } from './ordersSlice'
import { selectCreateProblem, selectCreating } from './selectors'

/**
 * The creation form.
 *
 * Nothing is added to the list here. The POST returns the created order and the socket
 * pushes it as well, and both go through the same version-guarded reducer — so the row
 * appears exactly once whichever arrives first, and it appears for every other open tab too.
 */
export function CreateOrderForm() {
  const dispatch = useAppDispatch()
  const creating = useAppSelector(selectCreating)
  const problem = useAppSelector(selectCreateProblem)
  const [description, setDescription] = useState('')

  // Measured after trimming because that is what gets sent — the server trims before
  // storing, but its length check runs on what it received.
  const trimmed = description.trim()
  const tooLong = trimmed.length > DESCRIPTION_MAX_LENGTH

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()

    if (trimmed.length === 0 || tooLong || creating) {
      return
    }

    // unwrap() would throw on rejection; the rejected state is already in the store and
    // rendered below, so there is nothing here to catch.
    const result = await dispatch(submitOrder(trimmed))

    if (submitOrder.fulfilled.match(result)) {
      setDescription('')
    }
  }

  return (
    <form className="card create-form" onSubmit={handleSubmit}>
      <label htmlFor="description">Новый заказ</label>

      <div className="create-row">
        <input
          id="description"
          value={description}
          onChange={(event) => setDescription(event.target.value)}
          placeholder="Описание закупаемой продукции"
          autoComplete="off"
          disabled={creating}
        />

        <button type="submit" disabled={creating || trimmed.length === 0 || tooLong}>
          {creating ? 'Создаём…' : 'Создать'}
        </button>
      </div>

      {/* Client-side length check mirrors the server validator rather than replacing it:
          it saves a round trip, and the server still refuses anything that gets past it. */}
      {tooLong && (
        <p className="field-error">
          Описание длиннее {DESCRIPTION_MAX_LENGTH} символов — сейчас {trimmed.length}.
        </p>
      )}

      {problem && (
        <ProblemMessage problem={problem} onDismiss={() => dispatch(createProblemCleared())} />
      )}
    </form>
  )
}
