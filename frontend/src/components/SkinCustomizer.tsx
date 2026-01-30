import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { X, RefreshCw, Palette, User } from 'lucide-react';
import { motion, AnimatePresence } from 'framer-motion';
import { useAccentColor } from '../contexts/AccentColorContext';
import { GetAuthDomain, GetUUID, GetNick } from '../../wailsjs/go/app/App';

interface SkinCustomizerProps {
    isOpen: boolean;
    onClose: () => void;
}

export const SkinCustomizer: React.FC<SkinCustomizerProps> = ({ isOpen, onClose }) => {
    const { t } = useTranslation();
    const { accentColor } = useAccentColor();
    const [isLoading, setIsLoading] = useState(true);
    const [authDomain, setAuthDomain] = useState('sessions.sanasol.ws');
    const [uuid, setUuid] = useState('');
    const [playerName, setPlayerName] = useState('');
    const [iframeKey, setIframeKey] = useState(0);

    // Load profile data
    useEffect(() => {
        if (isOpen) {
            loadProfile();
        }
    }, [isOpen]);

    const loadProfile = async () => {
        setIsLoading(true);
        try {
            const [domain, userUuid, nick] = await Promise.all([
                GetAuthDomain(),
                GetUUID(),
                GetNick()
            ]);
            if (domain) setAuthDomain(domain);
            if (userUuid) setUuid(userUuid);
            if (nick) setPlayerName(nick);
        } catch (err) {
            console.error('Failed to load profile:', err);
        }
        setIsLoading(false);
    };

    // Build customizer URL - embed directly
    const getCustomizerUrl = useCallback(() => {
        if (!uuid) return '';
        // Add embed parameter to remove chrome/header from the customizer
        return `https://${authDomain}/customizer/${uuid}?embed=true&theme=dark`;
    }, [authDomain, uuid]);

    const handleRefresh = () => {
        setIframeKey(prev => prev + 1);
    };

    if (!isOpen) return null;

    return (
        <AnimatePresence>
            <motion.div
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                className="fixed inset-0 z-[200] flex items-center justify-center bg-black/80 backdrop-blur-sm"
                onClick={(e) => e.target === e.currentTarget && onClose()}
            >
                <motion.div
                    initial={{ opacity: 0, scale: 0.95, y: 20 }}
                    animate={{ opacity: 1, scale: 1, y: 0 }}
                    exit={{ opacity: 0, scale: 0.95, y: 20 }}
                    className="bg-[#0f0f0f] border border-white/10 rounded-2xl shadow-2xl w-[95vw] max-w-6xl h-[90vh] max-h-[800px] overflow-hidden flex flex-col"
                >
                    {/* Header */}
                    <div className="flex items-center justify-between p-4 border-b border-white/10 flex-shrink-0">
                        <div className="flex items-center gap-3">
                            <Palette size={24} style={{ color: accentColor }} />
                            <div>
                                <h2 className="text-lg font-bold text-white">{t('Skin Customizer')}</h2>
                                <p className="text-xs text-white/50">{t('Customize your character appearance')}</p>
                            </div>
                        </div>
                        <div className="flex items-center gap-2">
                            <button
                                onClick={handleRefresh}
                                className="w-10 h-10 rounded-xl flex items-center justify-center text-white/60 hover:text-white hover:bg-white/10 transition-colors"
                                title={t('Refresh')}
                            >
                                <RefreshCw size={18} />
                            </button>
                            <button
                                onClick={onClose}
                                className="w-10 h-10 rounded-xl flex items-center justify-center text-white/60 hover:text-white hover:bg-white/10 transition-colors"
                            >
                                <X size={20} />
                            </button>
                        </div>
                    </div>

                    {/* Content */}
                    <div className="flex-1 overflow-hidden">
                        {isLoading ? (
                            <div className="flex items-center justify-center h-full">
                                <RefreshCw size={40} className="animate-spin text-white/40" />
                            </div>
                        ) : !uuid ? (
                            <div className="flex items-center justify-center h-full">
                                <div className="text-center">
                                    <User size={48} className="mx-auto mb-4 text-white/30" />
                                    <h3 className="text-white/60 text-lg mb-2">{t('No Profile Found')}</h3>
                                    <p className="text-white/40 text-sm">{t('Please set up your profile first in the Profile tab.')}</p>
                                </div>
                            </div>
                        ) : (
                            <div className="h-full flex flex-col">
                                {/* Embedded Customizer iframe */}
                                <div className="flex-1 relative">
                                    <iframe
                                        key={iframeKey}
                                        src={getCustomizerUrl()}
                                        className="w-full h-full border-0"
                                        title="Skin Customizer"
                                        allow="fullscreen"
                                        style={{ backgroundColor: '#0a0a0a' }}
                                    />
                                    
                                    {/* Loading overlay */}
                                    <div className="absolute inset-0 bg-[#0a0a0a] flex items-center justify-center pointer-events-none opacity-0 transition-opacity duration-300" id="customizer-loading">
                                        <RefreshCw size={32} className="animate-spin text-white/40" />
                                    </div>
                                </div>

                                {/* Info bar */}
                                <div className="p-3 border-t border-white/10 bg-[#0a0a0a] flex items-center justify-between">
                                    <div className="flex items-center gap-3">
                                        <div 
                                            className="w-8 h-8 rounded-full overflow-hidden border-2"
                                            style={{ borderColor: accentColor }}
                                        >
                                            <iframe
                                                src={`https://${authDomain}/avatar/${uuid}/head?bg=transparent`}
                                                width="32"
                                                height="32"
                                                frameBorder="0"
                                                className="w-full h-full"
                                                title="Avatar Head"
                                            />
                                        </div>
                                        <div>
                                            <p className="text-white text-sm font-medium">{playerName || 'Player'}</p>
                                            <p className="text-white/40 text-xs truncate max-w-[200px]">{uuid}</p>
                                        </div>
                                    </div>
                                    <div className="text-white/40 text-xs">
                                        {t('Changes are saved automatically on the server')}
                                    </div>
                                </div>
                            </div>
                        )}
                    </div>
                </motion.div>
            </motion.div>
        </AnimatePresence>
    );
};

export default SkinCustomizer;
