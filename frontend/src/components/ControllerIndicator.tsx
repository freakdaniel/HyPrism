import React from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import { ControllerIcon, ControllerType } from './icons/ControllerIcons';
import { useAccentColor } from '../contexts/AccentColorContext';

interface ControllerIndicatorProps {
  isConnected: boolean;
  controllerType: ControllerType;
  controllerName?: string;
}

const ControllerIndicator: React.FC<ControllerIndicatorProps> = ({
  isConnected,
  controllerType,
  controllerName,
}) => {
  const { accentColor } = useAccentColor();

  return (
    <AnimatePresence>
      {isConnected && (
        <motion.div
          initial={{ opacity: 0, scale: 0.8, y: 10 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.8, y: 10 }}
          className="fixed bottom-4 right-4 z-50"
        >
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-xl bg-black/40 backdrop-blur-md border border-white/10 shadow-lg"
            style={{ 
              boxShadow: `0 0 20px ${accentColor}20`,
              borderColor: `${accentColor}30`
            }}
          >
            <motion.div
              initial={{ rotate: -10 }}
              animate={{ rotate: 0 }}
              transition={{ type: 'spring', stiffness: 200 }}
            >
              <ControllerIcon
                type={controllerType}
                size={20}
                color={accentColor}
              />
            </motion.div>
            <div className="flex flex-col">
              <span className="text-xs text-white/90 font-medium">
                Controller Connected
              </span>
              {controllerName && (
                <span className="text-[10px] text-white/60 truncate max-w-[150px]">
                  {getShortName(controllerType, controllerName)}
                </span>
              )}
            </div>
            <div
              className="w-2 h-2 rounded-full animate-pulse"
              style={{ backgroundColor: accentColor }}
            />
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  );
};

// Get a user-friendly short name for the controller
function getShortName(type: ControllerType, fullName: string): string {
  switch (type) {
    case 'steamdeck':
      return 'Steam Deck';
    case 'playstation':
      if (fullName.toLowerCase().includes('dualsense')) return 'DualSense';
      if (fullName.toLowerCase().includes('dualshock 4')) return 'DualShock 4';
      if (fullName.toLowerCase().includes('ps5')) return 'PS5 Controller';
      if (fullName.toLowerCase().includes('ps4')) return 'PS4 Controller';
      return 'PlayStation Controller';
    case 'xbox':
      if (fullName.toLowerCase().includes('elite')) return 'Xbox Elite';
      if (fullName.toLowerCase().includes('series')) return 'Xbox Series';
      return 'Xbox Controller';
    case 'switch':
      if (fullName.toLowerCase().includes('pro')) return 'Switch Pro';
      if (fullName.toLowerCase().includes('joy-con')) return 'Joy-Con';
      return 'Switch Controller';
    default:
      // Truncate long names
      return fullName.length > 20 ? fullName.substring(0, 17) + '...' : fullName;
  }
}

export default ControllerIndicator;
