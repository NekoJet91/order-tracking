import { useDispatch, useSelector } from 'react-redux'
import type { AppDispatch, RootState } from './store'

/**
 * Typed replacements for the raw react-redux hooks.
 *
 * `useAppDispatch` matters more than it looks: the plain `useDispatch` is typed for actions
 * only, so dispatching a thunk through it either fails to compile or, worse, loses the
 * promise the caller wanted to await.
 */
export const useAppDispatch = useDispatch.withTypes<AppDispatch>()
export const useAppSelector = useSelector.withTypes<RootState>()
