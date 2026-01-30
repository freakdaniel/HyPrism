import React from 'react';

interface IconProps {
  size?: number;
  className?: string;
  color?: string;
}

// Steam Logo
export const SteamIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill={color}
    className={className}
  >
    <path d="M11.979 0C5.678 0 .511 4.86.022 11.037l6.432 2.658c.545-.371 1.203-.59 1.912-.59.063 0 .125.004.188.006l2.861-4.142V8.91c0-2.495 2.028-4.524 4.524-4.524 2.494 0 4.524 2.031 4.524 4.527s-2.03 4.525-4.524 4.525h-.105l-4.076 2.911c0 .052.004.105.004.159 0 1.875-1.515 3.396-3.39 3.396-1.635 0-3.016-1.173-3.331-2.727L.436 15.27C1.862 20.307 6.486 24 11.979 24c6.627 0 11.999-5.373 11.999-12S18.605 0 11.979 0zM7.54 18.21l-1.473-.61c.262.543.714.999 1.314 1.25 1.297.539 2.793-.076 3.332-1.375.263-.63.264-1.319.005-1.949s-.75-1.121-1.377-1.383c-.624-.26-1.29-.249-1.878-.03l1.523.63c.956.4 1.409 1.5 1.009 2.455-.397.957-1.497 1.41-2.454 1.012H7.54zm11.415-9.303c0-1.662-1.353-3.015-3.015-3.015-1.665 0-3.015 1.353-3.015 3.015 0 1.665 1.35 3.015 3.015 3.015 1.663 0 3.015-1.35 3.015-3.015zm-5.273-.005c0-1.252 1.013-2.266 2.265-2.266 1.249 0 2.266 1.014 2.266 2.266 0 1.251-1.017 2.265-2.266 2.265-1.253 0-2.265-1.014-2.265-2.265z"/>
  </svg>
);

// PlayStation Logo
export const PlayStationIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill={color}
    className={className}
  >
    <path d="M8.985 2.596v17.548l3.915 1.261V6.688c0-.69.304-1.151.794-.991.636.181.76.814.76 1.505v5.876c2.441 1.193 4.362-.002 4.362-3.153 0-3.237-1.126-4.675-4.438-5.827-1.307-.448-3.728-1.186-5.393-1.502zm4.656 16.242l6.296-2.275c.715-.258.826-.625.246-.818-.586-.192-1.637-.139-2.357.123l-4.185 1.476v-2.261l.24-.085s1.201-.428 2.89-.615c1.689-.187 3.758.025 5.387.72 1.839.608 2.046 1.501 1.581 2.106-.464.606-1.629 1.035-1.629 1.035l-8.469 3.072v-2.478zM1.348 18.754c-1.889-.65-2.209-2-1.395-2.632.756-.587 2.04-1.03 2.04-1.03l5.307-1.888v2.5l-3.825 1.373c-.715.26-.828.627-.247.822.581.193 1.633.136 2.352-.124l1.72-.632v2.235c-.143.025-.298.052-.457.079-1.669.271-3.45.099-5.495-.703z"/>
  </svg>
);

// Xbox Logo - X symbol in circle
export const XboxIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill="none"
    className={className}
  >
    <circle cx="12" cy="12" r="10" stroke={color} strokeWidth="2" />
    <path 
      d="M8 8L16 16M16 8L8 16" 
      stroke={color} 
      strokeWidth="2" 
      strokeLinecap="round" 
    />
  </svg>
);

// Nintendo Switch Logo - Joy-Con style
export const SwitchIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill="none"
    className={className}
  >
    {/* Left Joy-Con */}
    <rect x="2" y="3" width="8" height="18" rx="3" stroke={color} strokeWidth="1.5" />
    <circle cx="6" cy="8" r="2" fill={color} />
    {/* Right Joy-Con */}
    <rect x="14" y="3" width="8" height="18" rx="3" stroke={color} strokeWidth="1.5" />
    <circle cx="18" cy="16" r="2" fill={color} />
  </svg>
);

// Generic Gamepad Logo
export const GamepadIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 24 24"
    fill="none"
    stroke={color}
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    className={className}
  >
    <line x1="6" y1="12" x2="10" y2="12" />
    <line x1="8" y1="10" x2="8" y2="14" />
    <line x1="15" y1="13" x2="15.01" y2="13" />
    <line x1="18" y1="11" x2="18.01" y2="11" />
    <rect x="2" y="6" width="20" height="12" rx="2" />
  </svg>
);

// Backwards compatibility aliases
export const SteamDeckIcon = SteamIcon;

// Controller type detection helper
export type ControllerType = 'generic' | 'steam' | 'steamdeck' | 'playstation' | 'xbox' | 'switch';

export const ControllerIcon: React.FC<IconProps & { type?: ControllerType }> = ({ 
  type = 'generic', 
  ...props 
}) => {
  switch (type) {
    case 'steam':
    case 'steamdeck':
      return <SteamIcon {...props} />;
    case 'playstation':
      return <PlayStationIcon {...props} />;
    case 'xbox':
      return <XboxIcon {...props} />;
    case 'switch':
      return <SwitchIcon {...props} />;
    default:
      return <GamepadIcon {...props} />;
  }
};

// Controller button icons for button mapping display
export const ControllerButtonA: React.FC<IconProps> = ({ size = 24, className = '' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className}>
    <circle cx="12" cy="12" r="10" fill="#107C10" />
    <text x="12" y="16" textAnchor="middle" fill="white" fontSize="12" fontWeight="bold">A</text>
  </svg>
);

export const ControllerButtonB: React.FC<IconProps> = ({ size = 24, className = '' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className}>
    <circle cx="12" cy="12" r="10" fill="#E81123" />
    <text x="12" y="16" textAnchor="middle" fill="white" fontSize="12" fontWeight="bold">B</text>
  </svg>
);

export const ControllerButtonX: React.FC<IconProps> = ({ size = 24, className = '' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className}>
    <circle cx="12" cy="12" r="10" fill="#0078D4" />
    <text x="12" y="16" textAnchor="middle" fill="white" fontSize="12" fontWeight="bold">X</text>
  </svg>
);

export const ControllerButtonY: React.FC<IconProps> = ({ size = 24, className = '' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className}>
    <circle cx="12" cy="12" r="10" fill="#FFB900" />
    <text x="12" y="16" textAnchor="middle" fill="white" fontSize="12" fontWeight="bold">Y</text>
  </svg>
);

export const DPadIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill={color}>
    <path d="M9 2h6v6h6v6h-6v6H9v-6H3V8h6V2z" />
  </svg>
);

export const LeftStickIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill="none" stroke={color} strokeWidth="2">
    <circle cx="12" cy="12" r="9" />
    <circle cx="12" cy="12" r="4" fill={color} />
    <path d="M12 5v2M12 17v2M5 12h2M17 12h2" strokeLinecap="round" />
  </svg>
);

export const RightStickIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill="none" stroke={color} strokeWidth="2">
    <circle cx="12" cy="12" r="9" />
    <circle cx="12" cy="12" r="4" fill={color} />
    <path d="M12 5v2M12 17v2M5 12h2M17 12h2" strokeLinecap="round" />
  </svg>
);

export const LeftBumperIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill={color}>
    <path d="M4 8h16c1.1 0 2 .9 2 2v4c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2v-4c0-1.1.9-2 2-2z" />
    <text x="12" y="15" textAnchor="middle" fill="white" fontSize="8" fontWeight="bold">LB</text>
  </svg>
);

export const RightBumperIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill={color}>
    <path d="M4 8h16c1.1 0 2 .9 2 2v4c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2v-4c0-1.1.9-2 2-2z" />
    <text x="12" y="15" textAnchor="middle" fill="white" fontSize="8" fontWeight="bold">RB</text>
  </svg>
);

export const LeftTriggerIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill={color}>
    <path d="M4 6h16c1.1 0 2 .9 2 2v8c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V8c0-1.1.9-2 2-2z" />
    <text x="12" y="15" textAnchor="middle" fill="white" fontSize="8" fontWeight="bold">LT</text>
  </svg>
);

export const RightTriggerIcon: React.FC<IconProps> = ({ size = 24, className = '', color = 'currentColor' }) => (
  <svg width={size} height={size} viewBox="0 0 24 24" className={className} fill={color}>
    <path d="M4 6h16c1.1 0 2 .9 2 2v8c0 1.1-.9 2-2 2H4c-1.1 0-2-.9-2-2V8c0-1.1.9-2 2-2z" />
    <text x="12" y="15" textAnchor="middle" fill="white" fontSize="8" fontWeight="bold">RT</text>
  </svg>
);

export default ControllerIcon;
