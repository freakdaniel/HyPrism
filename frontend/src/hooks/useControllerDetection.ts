// Controller detection removed - this module provides stubs for backwards compatibility

interface Controller {
  id: string;
  name: string;
  type: string;
  index: number;
  connected: boolean;
  buttons: number;
  axes: number;
}

interface ControllerState {
  controllers: Controller[];
  activeController: Controller | null;
  isControllerMode: boolean;
}

export function useControllerDetection(): ControllerState {
  return {
    controllers: [],
    activeController: null,
    isControllerMode: false,
  };
}

export function useControllerInput(
  _onUp?: () => void,
  _onDown?: () => void,
  _onLeft?: () => void,
  _onRight?: () => void,
  _onSelect?: () => void,
  _onBack?: () => void
): void {
  // Controller input disabled
}

export default useControllerDetection;
